using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Client.Runtime.V1;

namespace IntoChat.OS.Client.Tests.Unit;

public sealed class SessionConnectionFacts
{
    [Fact]
    public async Task ReadinessWaitsForTheOutermostInitialReaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var remote = new FakeBrain();
        await using var session = await remote.OpenAsync(ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var ambient = AmbientSession.Enter(session, "acc-brain/csharp/ready", new Dictionary<string, string>(), stop.Token, "hash", "revision");
        await using var connection = await IntoChatOSClient.ConnectAsync([], ct);
        Assert.Empty(remote.Reports("status")); // Connecting a computational program does not initialize it.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reaction = connection.React<Changed>(Sources.Of(connection.Get<ISource>("source")), async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }, stop.Token);
        await entered.Task.WaitAsync(ct);
        Assert.NotEmpty(remote.Reports("subscribe"));
        Assert.Empty(remote.Reports("status"));
        release.SetResult();
        var report = await remote.StatusReports.Reader.ReadAsync(ct);
        Assert.Equal("Initialized", report["status"]!.GetValue<string>());
        Assert.Equal("hash", report["hash"]!.GetValue<string>());
        Assert.Equal("revision", report["assignmentRevision"]!.GetValue<string>());
        await stop.CancelAsync();
        await reaction.WaitAsync(ct);
    }

    [Fact]
    public async Task InitialReadinessRetriesTransientDeliveryWithoutRestartingTheReaction()
    {
        var ct = TestContext.Current.CancellationToken;
        var attempts = 0;
        var remote = new FakeBrain
        {
            ReportStatus = () => Interlocked.Increment(ref attempts) switch
        {
            1 => throw new HttpRequestException("transient connection failure"),
            <= 3 => HttpStatusCode.ServiceUnavailable,
            _ => HttpStatusCode.OK
        }
        };
        await using var session = await remote.OpenAsync(ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var ambient = AmbientSession.Enter(session, "acc-brain/csharp/ready", new Dictionary<string, string>(), stop.Token, "hash", "revision");
        await using var connection = await IntoChatOSClient.ConnectAsync([], ct);
        var rebuilds = 0;
        var reaction = connection.React<Changed>(Sources.Of(connection.Get<ISource>("source")), _ =>
        {
            Interlocked.Increment(ref rebuilds);
            return Task.CompletedTask;
        }, stop.Token);
        await remote.StatusReports.Reader.ReadAsync(ct);
        Assert.Equal(4, attempts);
        Assert.Equal(1, rebuilds);
        await stop.CancelAsync();
        await reaction.WaitAsync(ct);
    }

    [Fact]
    public async Task AStalledAcknowledgementTimesOutAndReleasesTheNextSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new ReportTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var brain = new FakeBrain
        {
            Acknowledge = async (_, token) =>
        {
            if (Interlocked.Increment(ref count) != 1) { return HttpStatusCode.OK; }
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return HttpStatusCode.OK;
        }
        };
        await using var session = await SessionConnection.OpenAsync(new Uri("https://brain.test/brains/acc-brain/"), "token-1", ContractsFingerprint.Of([typeof(Signal).Assembly]), brain, time, ct);
        var stalled = session.AcknowledgeAsync("stalled", 1, ct);
        await entered.Task.WaitAsync(ct);
        var next = session.AcknowledgeAsync("next", 1, ct);
        Assert.NotNull(time.Current);
        time.Current.Fire();
        await Assert.ThrowsAsync<TimeoutException>(() => stalled);
        await next.WaitAsync(ct);
        Assert.Equal(2, count);
        Assert.Equal("next", (await brain.Acknowledged.Reader.ReadAsync(ct))["subscription"]!.GetValue<string>());
    }

    private sealed class ReportTimeProvider : TimeProvider
    {
        public ReportTimer? Current { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(30), dueTime);
            return Current = new ReportTimer(callback, state);
        }

        public sealed class ReportTimer(TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task AcknowledgementReportsRemainBoundedWhenManySubscriptionsComplete()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new ConcurrentQueue<JsonArray>();
        var brain = new FakeBrain
        {
            Acknowledge = async (items, token) =>
        {
            batches.Enqueue(items);
            if (batches.Count != 1) { return HttpStatusCode.OK; }
            entered.SetResult();
            return await gate.Task.WaitAsync(token);
        }
        };
        await using var session = await brain.OpenAsync(ct);
        try
        {
            var first = session.AcknowledgeAsync("first", 1, ct);
            await entered.Task.WaitAsync(ct);
            var queued = Enumerable.Range(0, RuntimeProtocol.MaximumAcknowledgements + 7)
                .Select(index => session.AcknowledgeAsync("subscription-" + index, 1, ct)).ToArray();
            gate.SetResult(HttpStatusCode.OK);
            await Task.WhenAll(queued.Append(first));
            Assert.Equal(3, batches.Count);
            Assert.All(batches, batch => Assert.InRange(batch.Count, 1, RuntimeProtocol.MaximumAcknowledgements));
            Assert.Equal(RuntimeProtocol.MaximumAcknowledgements + 8, batches.Sum(batch => batch.Count));
        }
        finally { gate.TrySetResult(HttpStatusCode.OK); }
    }

    [Fact]
    public async Task FailedBatchesFaultEveryWaiterAndKeepUnrelatedTraceParentsSeparate()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parents = new ConcurrentQueue<string?>();
        var brain = new FakeBrain
        {
            Acknowledge = async (_, token) =>
        {
            parents.Enqueue(System.Diagnostics.Activity.Current?.ParentId);
            if (parents.Count != 1) { return HttpStatusCode.ServiceUnavailable; }
            entered.SetResult();
            return await gate.Task.WaitAsync(token);
        }
        };
        await using var session = await brain.OpenAsync(ct);
        try
        {
            var first = session.AcknowledgeAsync("first", 1, ct);
            await entered.Task.WaitAsync(ct);
            Task second, third, fourth;
            string? oneId, twoId;
            using (var one = new System.Diagnostics.Activity("one").Start())
            {
                oneId = one.Id;
                second = session.AcknowledgeAsync("second", 2, ct);
                third = session.AcknowledgeAsync("third", 3, ct);
            }
            using (var two = new System.Diagnostics.Activity("two").Start())
            {
                twoId = two.Id;
                fourth = session.AcknowledgeAsync("fourth", 4, ct);
            }
            gate.SetResult(HttpStatusCode.OK);
            await first;
            foreach (var failed in new[] { second, third, fourth })
            { await Assert.ThrowsAsync<HttpRequestException>(() => failed); }
            Assert.Equal([oneId, twoId], parents.Skip(1));
        }
        finally { gate.TrySetResult(HttpStatusCode.OK); }
    }

    [Fact]
    public async Task QueuedAcknowledgementsShareAReportAndWaitForItsResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstGate = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextGate = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = System.Threading.Channels.Channel.CreateUnbounded<JsonArray>();
        var count = 0;
        var brain = new FakeBrain
        {
            Acknowledge = async (items, token) =>
        {
            var index = Interlocked.Increment(ref count);
            received.Writer.TryWrite(items);
            return await (index == 1 ? firstGate : nextGate).Task.WaitAsync(token);
        }
        };
        await using var session = await brain.OpenAsync(ct);
        try
        {
            var first = session.AcknowledgeAsync("first", 1, ct);
            await received.Reader.ReadAsync(ct);
            var second = session.AcknowledgeAsync("second", 2, ct);
            var third = session.AcknowledgeAsync("third", 3, ct);
            var newer = session.AcknowledgeAsync("second", 5, ct);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            firstGate.SetResult(HttpStatusCode.OK);
            await first;
            var batch = await received.Reader.ReadAsync(ct);
            Assert.Equal(2, batch.Count);
            Assert.Contains(batch, item => item!["subscription"]!.GetValue<string>() == "second" && item["watermark"]!.GetValue<long>() == 5);
            Assert.Contains(batch, item => item!["subscription"]!.GetValue<string>() == "third" && item["watermark"]!.GetValue<long>() == 3);
            Assert.False(newer.IsCompleted);
            nextGate.SetResult(HttpStatusCode.OK);
            await Task.WhenAll(second, third, newer);
            Assert.Equal(2, count);
        }
        finally { firstGate.TrySetResult(HttpStatusCode.OK); nextGate.TrySetResult(HttpStatusCode.OK); }
    }

    // One report covers every batch queued for a subscription: the highest watermark, and the time and
    // batches of all of them, so the brain records what the runs cost together.
    [Fact]
    public async Task CoalescedAcknowledgementsSumTheirTimeAndBatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstGate = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = System.Threading.Channels.Channel.CreateUnbounded<JsonArray>();
        var count = 0;
        var brain = new FakeBrain
        {
            Acknowledge = async (items, token) =>
            {
                var index = Interlocked.Increment(ref count);
                received.Writer.TryWrite(items);
                return index == 1 ? await firstGate.Task.WaitAsync(token) : HttpStatusCode.OK;
            }
        };
        await using var session = await brain.OpenAsync(ct);
        try
        {
            var first = session.AcknowledgeAsync("first", 1, ct);
            await received.Reader.ReadAsync(ct);
            var queued = new[]
            {
                session.AcknowledgeAsync("second", 2, ct, cpuMicroseconds: 100, wallMicroseconds: 1_000),
                session.AcknowledgeAsync("second", 3, ct, cpuMicroseconds: 20, wallMicroseconds: 300),
            };
            firstGate.SetResult(HttpStatusCode.OK);
            await first;

            var report = Assert.Single(await received.Reader.ReadAsync(ct))!;
            await Task.WhenAll(queued);

            Assert.Equal("second", report["subscription"]!.GetValue<string>());
            Assert.Equal(3, report["watermark"]!.GetValue<long>());
            Assert.Equal(120, report["cpuMicroseconds"]!.GetValue<long>());
            Assert.Equal(1_300, report["wallMicroseconds"]!.GetValue<long>());
            Assert.Equal(2, report["batches"]!.GetValue<int>());
        }
        finally { firstGate.TrySetResult(HttpStatusCode.OK); }
    }

    [Fact]
    public async Task AFailedAcknowledgementCanBeRetriedAndDisposalSettlesOutstandingWaiters()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = System.Threading.Channels.Channel.CreateUnbounded<int>();
        var count = 0;
        var brain = new FakeBrain
        {
            Acknowledge = async (_, token) =>
        {
            var index = Interlocked.Increment(ref count);
            received.Writer.TryWrite(index);
            return index == 1 ? HttpStatusCode.ServiceUnavailable : index == 2 ? HttpStatusCode.OK : await gate.Task.WaitAsync(token);
        }
        };
        var session = await brain.OpenAsync(ct);
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => session.AcknowledgeAsync("same", 1, ct));
            await session.AcknowledgeAsync("same", 1, ct);
            Assert.Equal(2, count);
            while (received.Reader.TryRead(out _)) { }
            var inFlight = session.AcknowledgeAsync("first", 1, ct);
            await received.Reader.ReadAsync(ct);
            var queued = session.AcknowledgeAsync("second", 2, ct);
            await session.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        }
        finally { gate.TrySetResult(HttpStatusCode.OK); await session.DisposeAsync(); }
    }

    [Fact]
    public async Task CancellingOneAcknowledgementWaitDoesNotCancelAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gate = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var brain = new FakeBrain
        {
            Acknowledge = async (_, token) =>
        { entered.TrySetResult(); return await gate.Task.WaitAsync(token); }
        };
        await using var session = await brain.OpenAsync(ct);
        try
        {
            var cancelled = session.AcknowledgeAsync("first", 1, stopped.Token);
            await entered.Task.WaitAsync(ct);
            var retained = session.AcknowledgeAsync("second", 2, ct);
            await stopped.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.False(retained.IsCompleted);
            gate.SetResult(HttpStatusCode.OK);
            await retained;
        }
        finally { gate.TrySetResult(HttpStatusCode.OK); }
    }

    [DigitalBrain.Signal("tests.client.changed")] public sealed record Changed(int Value) : DigitalBrain.Signal;
    [DigitalBrain.Contracts.NeuronAlias("tests.client.source")] public interface ISource { Task<int> Double(int value); Task Refuse(); }

    [Fact]
    public async Task InvokeGoesToTheInvokeRouteWithTheBehaviorId()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new FakeBrain();
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", ct);

        var doubled = await connection.Get<ISource>("source").Double(21);

        Assert.Equal(42, doubled);
        var invoke = Assert.Single(brain.Requests, request => request.Path.EndsWith(RuntimeProtocol.Invoke, StringComparison.Ordinal));
        Assert.Equal("acc-brain/csharp/a", invoke.Body!["behavior"]!.GetValue<string>());
        Assert.Equal(typeof(ISource).FullName, invoke.Body!["contract"]!.GetValue<string>());
        Assert.Equal("Bearer token-1", invoke.Authorization);
    }

    // The edge resolves the wire key as a raw grain key (RuntimeEdge resolves the contract at invocation.Key), so a
    // brain-relative id must leave the sandbox as the same "{brain}/{relative}" key the raw-key path sends.
    [Fact]
    public async Task ARelativeNeuronCrossesTheEdgeWithTheRawKeyOfItsBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new FakeBrain();
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", ct);

        await connection.Neuron<ISource>("csharp/source").Double(1);
        await connection.Get<ISource>("acc-brain/csharp/source").Double(1);

        var keys = brain.Requests.Where(request => request.Path.EndsWith(RuntimeProtocol.Invoke, StringComparison.Ordinal))
            .Select(request => request.Body!["key"]!.GetValue<string>()).ToArray();
        Assert.Equal(["acc-brain/csharp/source", "acc-brain/csharp/source"], keys);
        Assert.Equal(connection.Get<ISource>("acc-brain/csharp/source"), connection.Neuron<ISource>("csharp/source"));
    }

    [Fact]
    public async Task OnSubscribesThenYieldsBatchSignalsThenAcknowledges()
    {
        var ct = TestContext.Current.CancellationToken;
        // The second batch is the brain's next push: the unacknowledged pair again plus one new signal.
        var brain = new FakeBrain { Streams = { Batch("s1", 2, 1, 2) + Batch("s1", 3, 1, 2, 3) } };
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", ct);

        var seen = new List<int>();
        await foreach (var signal in connection.On<Changed>(connection.Get<ISource>("source"), ct))
        {
            seen.Add(signal.Value);
            if (seen.Count == 3) { break; }
        }

        Assert.Equal([1, 2, 3], seen);
        var subscribe = Assert.Single(brain.Reports("subscribe"));
        // A [Signal] record is subscribed by its alias.
        Assert.Equal("tests.client.changed", subscribe["signal"]!.GetValue<string>());
        Assert.Equal(2, (await brain.Acknowledged.Reader.ReadAsync(ct))["watermark"]!.GetValue<long>());
    }

    [Fact]
    public async Task ReconciliationRunsAfterRegistrationAndBeforeQueuedSignals()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new FakeBrain { Streams = { Batch("s1", 1, 1) } };
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", ct);
        var order = new List<string>();
        var reconciling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = connection.On<Changed>(connection.Get<ISource>("source"), async () =>
        {
            Assert.Single(brain.Reports("subscribe"));
            reconciling.SetResult();
            await reconciled.Task.WaitAsync(ct);
            order.Add("snapshot");
        }, ct);
        Assert.Empty(order);
        await using var reader = stream.GetAsyncEnumerator(ct);
        var pending = reader.MoveNextAsync().AsTask();
        await reconciling.Task.WaitAsync(ct);
        Assert.False(pending.IsCompleted);
        reconciled.SetResult();
        Assert.True(await pending);
        order.Add("signal");
        Assert.Equal(["snapshot", "signal"], order);
    }

    // A click's trace continues through the stream into the calls the behavior makes in reaction.
    [Fact]
    public async Task ASignalsTraceTravelsWithTheCallsItCauses()
    {
        var ct = TestContext.Current.CancellationToken;
        const string trace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        var brain = new FakeBrain { Streams = { Batch("s1", 1, trace, 1) } };
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", ct);

        await foreach (var signal in connection.On<Changed>(connection.Get<ISource>("source"), ct))
        {
            await connection.Get<ISource>("source").Double(signal.Value);
            break;
        }

        var invoke = Assert.Single(brain.Requests, request => request.Path.EndsWith(RuntimeProtocol.Invoke, StringComparison.Ordinal));
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", invoke.TraceParent!.Split('-')[1]);
    }

    [Fact]
    public async Task ReconnectResumesFromTheLastAcknowledgedWatermark()
    {
        var ct = TestContext.Current.CancellationToken;
        // The first stream drops after one batch; the brain resends the same batch plus one new signal.
        var brain = new FakeBrain { Streams = { Batch("s1", 2, 1, 2), Batch("s1", 3, 1, 2, 3) } };
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", ct);

        var seen = new List<int>();
        await foreach (var signal in connection.On<Changed>(connection.Get<ISource>("source"), ct))
        {
            seen.Add(signal.Value);
            if (seen.Count == 3) { break; }
        }

        Assert.Equal([1, 2, 3], seen);
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("enumerator")]
    [InlineData("behavior")]
    public async Task CancellingAnySubscriptionLifetimeEndsAnIdleRead(string lifetime)
    {
        var ct = TestContext.Current.CancellationToken;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var session = await new FakeBrain().OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", lifetime == "behavior" ? stopping.Token : ct);
        await using var reader = connection.On<Changed>(connection.Get<ISource>("source"), lifetime == "argument" ? stopping.Token : ct)
            .GetAsyncEnumerator(lifetime == "enumerator" ? stopping.Token : ct);

        var pending = reader.MoveNextAsync().AsTask();
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3), ct));
    }

    [Fact]
    public async Task ARefusedFingerprintFailsTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new FakeBrain { RefuseFingerprint = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.OpenAsync(ct));
    }

    [Fact]
    public async Task CapacityRefusalTravelsThroughTheClientVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new FakeBrain();
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", ct);

        var error = await Assert.ThrowsAsync<ScriptRefusalException>(() => connection.Get<ISource>("source").Refuse());

        Assert.Equal(("The brain is at capacity.", "CapacityUnavailableException"), (error.Message, error.Code));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ATransientHttpRefusalRetainsItsStatusWithoutRetryingTheInvocation(HttpStatusCode status)
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new FakeBrain { InvocationResponse = () => new(status) { Content = new StringContent("Temporary edge failure") } };
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", ct);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.Get<ISource>("source").Double(21));

        Assert.Equal(status, error.StatusCode);
        Assert.Contains("ISource.Double", error.Message, StringComparison.Ordinal);
        Assert.Contains("Temporary edge failure", error.Message, StringComparison.Ordinal);
        Assert.Single(brain.Requests, request => request.Path.EndsWith(RuntimeProtocol.Invoke, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ADefinitiveHttpRefusalKeepsItsExistingException(HttpStatusCode status)
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new FakeBrain { InvocationResponse = () => new(status) { Content = new StringContent("Refused input") } };
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Get<ISource>("source").Double(21));

        Assert.Contains("Refused input", error.Message, StringComparison.Ordinal);
        Assert.Single(brain.Requests, request => request.Path.EndsWith(RuntimeProtocol.Invoke, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARevisionConflictKeepsItsSpecializedException()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new FakeBrain
        {
            InvocationResponse = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Conflict)
            { Content = new StringContent("{\"message\":\"Changed since read\",\"currentRevision\":7}") };
            response.Headers.Add(RuntimeProtocol.RefusalHeader, RevisionConflictException.Code);
            return response;
        }
        };
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", ct);

        var error = await Assert.ThrowsAsync<RevisionConflictException>(() => connection.Get<ISource>("source").Double(21));

        Assert.Equal(7, error.CurrentRevision);
        Assert.Equal("Changed since read", error.Message);
    }

    [Fact]
    public async Task ConnectAsyncOutsideASessionThrows()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => IntoChatOSClient.ConnectAsync([], TestContext.Current.CancellationToken));
        Assert.Contains("sandbox session", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsyncInsideASessionReadsTheBehaviorsSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await new FakeBrain().OpenAsync(ct);
        using (AmbientSession.Enter(session, "acc-brain/csharp/a", new Dictionary<string, string> { ["TimerId"] = "tea" }, ct))
        {
            await using var connection = await IntoChatOSClient.ConnectAsync([], ct);
            Assert.Equal("tea", connection.Setting("TimerId"));
            Assert.Null(connection.Setting("Missing"));
        }
    }

    private static async Task<IntoChatOSConnection> Connect(SessionConnection session, string behavior, CancellationToken ct)
    {
        using (AmbientSession.Enter(session, behavior, new Dictionary<string, string>(), ct))
        { return await IntoChatOSClient.ConnectAsync([], ct); }
    }

    private static string Batch(string subscription, long watermark, params int[] values) => Batch(subscription, watermark, null, values);

    private static string Batch(string subscription, long watermark, string? trace, params int[] values)
        => $"event: {SignalBatchEvent.Kind}\ndata: {JsonSerializer.Serialize(new SignalBatchEvent(subscription, [.. values.Select(value => JsonSerializer.Serialize(new { value }))], watermark, trace), RuntimeProtocol.Json)}\n\n";

    // Speaks the brain side of the three routes. Each GET of the stream serves the next scripted body
    // after a ready event, then ends; once the script runs out, a stream stays open until cancelled.
    private sealed class FakeBrain : HttpMessageHandler
    {
        public Func<HttpStatusCode>? ReportStatus { get; init; }
        public Func<HttpResponseMessage>? InvocationResponse { get; init; }
        public List<string> Streams { get; } = [];
        public bool RefuseFingerprint { get; init; }
        public Func<JsonArray, CancellationToken, Task<HttpStatusCode>>? Acknowledge { get; init; }
        public ConcurrentQueue<(string Path, string? Authorization, JsonNode? Body, string? TraceParent)> Requests { get; } = new();
        public System.Threading.Channels.Channel<JsonNode> Acknowledged { get; } = System.Threading.Channels.Channel.CreateUnbounded<JsonNode>();
        public System.Threading.Channels.Channel<JsonNode> StatusReports { get; } = System.Threading.Channels.Channel.CreateUnbounded<JsonNode>();
        private int _streams;

        public Task<SessionConnection> OpenAsync(CancellationToken ct)
            => SessionConnection.OpenAsync(new Uri("https://brain.test/brains/acc-brain/"), "token-1", ContractsFingerprint.Of([typeof(Signal).Assembly]), this, ct);

        public IEnumerable<JsonNode> Reports(string member)
            => Requests.Where(request => request.Path.EndsWith(RuntimeProtocol.Report, StringComparison.Ordinal) && request.Body?[member] is not null).Select(request => request.Body![member]!);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var body = text.Length == 0 ? null : JsonNode.Parse(text);
            Requests.Enqueue((path, request.Headers.Authorization?.ToString(), body,
                request.Headers.TryGetValues("traceparent", out var traces) ? traces.Single() : null));
            if (path.EndsWith(RuntimeProtocol.Stream, StringComparison.Ordinal))
            {
                var index = Interlocked.Increment(ref _streams) - 1;
                var events = $"event: {ReadyEvent.Kind}\ndata: {{\"session\":\"s\"}}\n\n" + (index < Streams.Count ? Streams[index] : "");
                Stream content = index < Streams.Count ? new MemoryStream(Encoding.UTF8.GetBytes(events)) : new OpenStream(Encoding.UTF8.GetBytes(events));
                return new(HttpStatusCode.OK) { Content = new StreamContent(content) { Headers = { ContentType = new("text/event-stream") } } };
            }
            if (path.EndsWith(RuntimeProtocol.Invoke, StringComparison.Ordinal))
            {
                if (InvocationResponse is not null) { return InvocationResponse(); }
                if (body!["method"]!.GetValue<string>() == nameof(ISource.Refuse))
                {
                    var refused = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("The brain is at capacity.") };
                    refused.Headers.Add(RuntimeProtocol.RefusalHeader, "CapacityUnavailableException");
                    return refused;
                }
                return new(HttpStatusCode.OK) { Content = new StringContent((body["arguments"]![0]!.GetValue<int>() * 2).ToString(System.Globalization.CultureInfo.InvariantCulture)) };
            }
            if (body?["fingerprint"] is not null) { return new(RefuseFingerprint ? HttpStatusCode.Conflict : HttpStatusCode.OK); }
            if (body?["subscribe"] is not null) { return new(HttpStatusCode.OK) { Content = new StringContent("{\"subscription\":\"s1\"}") }; }
            if (body?["status"] is { } statusReport)
            {
                var status = ReportStatus?.Invoke() ?? HttpStatusCode.OK;
                if (status == HttpStatusCode.OK) { StatusReports.Writer.TryWrite(statusReport.DeepClone()); }
                return new(status);
            }
            var acknowledgements = body?["acknowledgements"] as JsonArray
                ?? (body?["acknowledge"] is { } single ? new JsonArray(single.DeepClone()) : null);
            if (acknowledgements is not null)
            {
                var status = Acknowledge is null ? HttpStatusCode.OK : await Acknowledge(acknowledgements, cancellationToken);
                if (status == HttpStatusCode.OK)
                { foreach (var acknowledge in acknowledgements) { Acknowledged.Writer.TryWrite(acknowledge!.DeepClone()); } }
                return new(status);
            }
            return new(HttpStatusCode.OK);
        }

        // A stream that has sent its bytes and then stays open until the reader goes away.
        private sealed class OpenStream(byte[] head) : Stream
        {
            private readonly MemoryStream _head = new(head);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await _head.ReadAsync(buffer, cancellationToken);
                if (read > 0) { return read; }
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
