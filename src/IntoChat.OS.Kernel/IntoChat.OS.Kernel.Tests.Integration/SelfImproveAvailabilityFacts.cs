using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Flutter.Layout;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Integrations;
using IntoChat.OS.Microsoft.Aspire;
using IntoChat.OS.Microsoft.CSharp.Catalog;

namespace IntoChat.OS.Kernel.Tests.Integration;

// Executes shipped synapses and their shared helper through the real runtime client; only the HTTP
// boundary is controlled. No deployment driver, credentials, host or persisted state is involved.
public sealed class SelfImproveAvailabilityFacts
{
    private const string Unavailable = "Self-improve is unavailable on this host because deployment is not configured.";
    private const string Change = """{"branch":"fixture","summary":"Fixture change","files":{"fixture.cs":"// fixture"},"plan":{"calls":[],"spans":[],"budget":"00:01:00"}}""";
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);
    private static readonly Lazy<IReadOnlyDictionary<string, CompiledBehavior>> Scripts = new(Compile);

    [Theory]
    [InlineData("list.cs")]
    [InlineData("open.cs")]
    public async Task AnUnavailableHostShowsAnExplanationAndKeepsItsInitialWait(string script)
    {
        using var edge = new Edge();
        await using var running = await Running.Start(edge, script);
        await running.Wait(() => edge.Initialized > 0);
        if (script == "open.cs") { edge.Request("open", ""); }
        await running.Wait(() => edge.Text("revisions") == Unavailable);
        Assert.DoesNotContain(edge.Calls, call => call.Contract == typeof(IDeployment).FullName || call.Contract == typeof(ICandidate).FullName);
        Assert.DoesNotContain(edge.Calls, call => call.Contract == RuntimeProtocol.KernelContract && call.Method == RuntimeProtocol.KernelInstances);
        Assert.True(edge.Subscriptions.Count > 0);
        Assert.False(running.Execution.IsCompleted);
    }

    [Fact]
    public async Task ARefusalClearsTheLastShaDeduplicatesAndHandlesTheNextRequest()
    {
        using var edge = new Edge();
        edge.SetText("last", "old-sha");
        await using var running = await Running.Start(edge, "request.cs");
        await running.Wait(() => edge.Initialized > 0);
        var id = edge.Request("request", Change);
        await running.Wait(() => edge.Text("handled") == id.ToString("N") && edge.Text("revisions") == Unavailable);
        Assert.Equal("", edge.Text("last"));
        Assert.Equal("Request refused: " + Unavailable, edge.Text("report"));
        Assert.Equal(Unavailable, edge.Text("revisions"));
        AssertNoDeploymentActions(edge);
        edge.Available = true;
        edge.Request("request", Change, id);
        var next = edge.Request("request", Change);
        await running.Wait(() => edge.Text("handled") == next.ToString("N"));
        Assert.Equal("new-sha", edge.Text("last"));
        var proposal = Assert.Single(edge.Calls, call => call.Method == nameof(ICandidates.Propose));
        Assert.Equal("Fixture change", proposal.Arguments[0].Deserialize<CandidateProposal>(RuntimeProtocol.Json)!.Summary);
        Assert.True(edge.Subscriptions.Count > 0);
        Assert.False(running.Execution.IsCompleted);
    }

    [Theory]
    [InlineData("list.cs")]
    [InlineData("open.cs")]
    public async Task AConfiguredListRendersCandidatesAndRechecksAfterCapabilityRemoval(string script)
    {
        using var edge = new Edge { Available = true, HasCandidate = true };
        await using var running = await Running.Start(edge, script);
        await running.Wait(() => edge.Initialized > 0);
        if (script == "open.cs") { edge.Request("open", ""); }
        await running.Wait(() => edge.Text("revisions").Contains("Live: `current-sha` — Steady", StringComparison.Ordinal));
        Assert.Contains("`candidate-sha` — Healthy — Existing change", edge.Text("revisions"), StringComparison.Ordinal);
        var deployments = edge.Calls.Count(call => call.Contract == typeof(IDeployment).FullName);
        var candidateReads = edge.Calls.Count(call => call.Contract == typeof(ICandidate).FullName);
        edge.Available = false;
        if (script == "open.cs") { edge.Request("open", ""); }
        else { edge.Changed(Edge.CandidateKey); }
        await running.Wait(() => edge.Text("revisions") == Unavailable);
        Assert.Equal(deployments, edge.Calls.Count(call => call.Contract == typeof(IDeployment).FullName));
        Assert.Equal(candidateReads, edge.Calls.Count(call => call.Contract == typeof(ICandidate).FullName));
        Assert.False(running.Execution.IsCompleted);
    }

    [Fact]
    public async Task AConfiguredRequestRechecksCapabilityBeforeItsNextProposal()
    {
        using var edge = new Edge { Available = true };
        await using var running = await Running.Start(edge, "request.cs");
        await running.Wait(() => edge.Initialized > 0);
        var first = edge.Request("request", Change);
        await running.Wait(() => edge.Text("handled") == first.ToString("N") && edge.Text("revisions").Contains("Live:", StringComparison.Ordinal));
        Assert.Equal("new-sha", edge.Text("last"));
        edge.Available = false;
        var second = edge.Request("request", Change);
        await running.Wait(() => edge.Text("handled") == second.ToString("N"));
        Assert.Equal("", edge.Text("last"));
        Assert.Equal("Request refused: " + Unavailable, edge.Text("report"));
        Assert.Single(edge.Calls, call => call.Method == nameof(ICandidates.Propose));
    }

    [Theory]
    [InlineData("healthy.cs", CandidateStage.Healthy, false)]
    [InlineData("verified.cs", CandidateStage.Verified, false)]
    [InlineData("healthy.cs", CandidateStage.Healthy, true)]
    [InlineData("verified.cs", CandidateStage.Verified, true)]
    public async Task ExistingCandidateCallbacksRequireCurrentCapability(string script, CandidateStage stage, bool available)
    {
        using var edge = new Edge { Available = available, HasCandidate = true, Stage = stage };
        await using var running = await Running.Start(edge, script);
        await running.Wait(() => edge.Initialized > 0);
        edge.Changed();
        await running.Wait(() => available ? edge.Acknowledged > 0 : edge.Text("revisions") == Unavailable);
        var actions = edge.Calls.Where(call => call.Method is nameof(ICandidate.Verify) or nameof(ICandidate.Promote)).ToArray();
        if (available) { Assert.Equal(stage == CandidateStage.Healthy ? nameof(ICandidate.Verify) : nameof(ICandidate.Promote), Assert.Single(actions).Method); }
        else
        {
            Assert.Empty(actions);
            Assert.Equal(Unavailable, edge.Text("revisions"));
            AssertNoDeploymentActions(edge);
        }
        Assert.True(edge.Subscriptions.Count > 0);
        Assert.False(running.Execution.IsCompleted);
    }

    [Theory]
    [InlineData(CandidateStage.Starting, CandidateStage.Healthy)]
    [InlineData(CandidateStage.Verifying, CandidateStage.Verified)]
    public async Task AnInterruptedCandidateResumesWithoutAnotherSignalAndReleasesTheNextProposal(CandidateStage before, CandidateStage interrupted)
    {
        using var edge = new Edge { Available = true, HasCandidate = true, Stage = before, QueueProposals = true };
        await using var running = await Running.Start(edge, "healthy.cs", "verified.cs", "request.cs");
        await running.Wait(() => edge.Initialized == 3);
        edge.Changed();
        await running.Wait(() => edge.Subscriptions.Values.Any(subscription => subscription.Behavior.EndsWith("/verified.cs", StringComparison.Ordinal)
            && subscription.Key == Edge.CandidateKey && subscription.Signal == nameof(CandidateChanged)));
        if (interrupted == CandidateStage.Healthy)
        {
            await running.Wait(() => edge.Subscriptions.Values.Any(subscription => subscription.Behavior.EndsWith("/healthy.cs", StringComparison.Ordinal)
                && subscription.Key == Edge.CandidateKey && subscription.Signal == nameof(CandidateChanged)));
        }
        edge.Available = false;
        edge.Stage = interrupted;
        edge.Changed(Edge.CandidateKey);
        await running.Wait(() => edge.Text("revisions") == Unavailable && edge.UnavailableCallbacks.Count == (interrupted == CandidateStage.Healthy ? 2 : 1));
        AssertNoDeploymentActions(edge);

        // Restoration emits no signal. The next request queues behind the still-live first candidate;
        // only resuming and promoting that candidate can start the queued proposal.
        edge.PauseCallbackDiscovery = true;
        edge.Available = true;
        edge.Request("request", Change);
        await running.Wait(() => edge.NextStage == CandidateStage.Queued);
        Assert.Equal(interrupted, edge.Stage);
        edge.ResumeCallbacks.TrySetResult();
        await running.Wait(() => edge.NextStage == CandidateStage.Starting);
        Assert.Equal(CandidateStage.Promoted, edge.Stage);
        var firstActions = edge.Calls.Where(call => call.Key == Edge.CandidateKey && call.Method is nameof(ICandidate.Verify) or nameof(ICandidate.Promote)).ToArray();
        Assert.Equal(interrupted == CandidateStage.Healthy ? [nameof(ICandidate.Verify), nameof(ICandidate.Promote)] : [nameof(ICandidate.Promote)],
            firstActions.Select(call => call.Method));

        var nextKey = "fixture-brain/aspire/candidate/new-sha";
        await running.Wait(() => edge.Subscriptions.Values.Count(subscription => subscription.Key == nextKey && subscription.Signal == nameof(CandidateChanged)) == 2);
        edge.NextStage = CandidateStage.Healthy;
        edge.Changed(nextKey, "new-sha");
        await running.Wait(() => edge.NextStage == CandidateStage.Promoted);
        Assert.Equal([nameof(ICandidate.Verify), nameof(ICandidate.Promote)], edge.Calls
            .Where(call => call.Key == nextKey && call.Method is nameof(ICandidate.Verify) or nameof(ICandidate.Promote)).Select(call => call.Method));
        Assert.False(running.Execution.IsCompleted);
    }

    [Theory]
    [InlineData("healthy.cs", CandidateStage.Starting, CandidateStage.Healthy, CandidateStage.Verified)]
    [InlineData("verified.cs", CandidateStage.Verifying, CandidateStage.Verified, CandidateStage.Promoted)]
    public async Task RestorationReadsTheCurrentStageBeforeRepeatingAnAction(string script, CandidateStage before, CandidateStage interrupted, CandidateStage progressed)
    {
        using var edge = new Edge { Available = true, HasCandidate = true, Stage = before };
        await using var running = await Running.Start(edge, script);
        await running.Wait(() => edge.Initialized > 0);
        edge.Changed();
        await running.Wait(() => edge.Subscriptions.Values.Any(subscription => subscription.Key == Edge.CandidateKey && subscription.Signal == nameof(CandidateChanged)));
        edge.Available = false;
        edge.Stage = interrupted;
        edge.Changed(Edge.CandidateKey);
        await running.Wait(() => edge.Text("revisions") == Unavailable);
        AssertNoDeploymentActions(edge);
        var reads = edge.Calls.Count(call => call.Key == Edge.CandidateKey && call.Method == nameof(ICandidate.Read));
        edge.Stage = progressed;
        edge.Available = true;
        await running.Wait(() => edge.Calls.Count(call => call.Key == Edge.CandidateKey && call.Method == nameof(ICandidate.Read)) > reads);
        AssertNoDeploymentActions(edge);
        Assert.False(running.Execution.IsCompleted);
    }

    [Theory]
    [InlineData("healthy.cs", CandidateStage.Healthy)]
    [InlineData("verified.cs", CandidateStage.Verified)]
    public async Task DiscoveryFailureWhileACandidateWaitsRemainsAnError(string script, CandidateStage stage)
    {
        using var edge = new Edge { Stage = stage };
        await using var running = await Running.Start(edge, script);
        await running.Wait(() => edge.Initialized > 0);
        edge.Changed();
        await running.Wait(() => edge.Text("revisions") == Unavailable);
        edge.FailDiscovery = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(running.Complete);
        Assert.Contains("Discovery fixture failed", error.Message, StringComparison.Ordinal);
        AssertNoDeploymentActions(edge);
    }

    [Theory]
    [InlineData("list.cs", true)]
    [InlineData("list.cs", false)]
    [InlineData("request.cs", true)]
    public async Task UnexpectedDiscoveryOrHostErrorsRemainErrors(string script, bool discovery)
    {
        using var edge = new Edge { Available = true, FailDiscovery = discovery, FailDeployment = !discovery };
        await using var running = await Running.Start(edge, script);
        if (script == "request.cs")
        {
            await running.Wait(() => edge.Initialized > 0);
            edge.Request("request", Change);
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(running.Complete);
        Assert.Contains(discovery ? "Discovery fixture failed" : "Host fixture failed", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Unavailable, edge.Text("revisions"), StringComparison.Ordinal);
        Assert.DoesNotContain(Unavailable, edge.Text("report"), StringComparison.Ordinal);
        if (discovery) { AssertNoDeploymentActions(edge); }
    }

    private static void AssertNoDeploymentActions(Edge edge)
        => Assert.DoesNotContain(edge.Calls, call => call.Contract == typeof(IDeployment).FullName || call.Method is nameof(ICandidates.Propose) or nameof(ICandidate.Verify) or nameof(ICandidate.Promote));

    private static IReadOnlyDictionary<string, CompiledBehavior> Compile()
    {
        var feature = new FeatureDirectory(SolutionProjects.Features).Load().Single(item => item.Owner == "os" && item.Bundle.Name == "selfimprove");
        var compiled = new CSharpScriptCheck().Compile(feature.Bundle.Files.Where(file => AppFiles.IsProgram(file.Key)).ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal));
        Assert.True(compiled.Success, string.Join("\n", compiled.Errors));
        return compiled.Assemblies;
    }

    private sealed class Running(SessionConnection session, CancellationTokenSource stop, Task execution) : IAsyncDisposable
    {
        private bool _failureObserved;
        public Task Execution => execution;

        public static async Task<Running> Start(Edge edge, params string[] scripts)
        {
            var ct = TestContext.Current.CancellationToken;
            var session = await SessionConnection.OpenAsync(new("http://brain.test/brains/fixture-brain/"), "fixture-token", "fixture-fingerprint", edge, ct);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var executions = new List<Task>();
            foreach (var script in scripts)
            {
                using var ambient = AmbientSession.Enter(session, Edge.App + "/" + script, new Dictionary<string, string>
                { ["App"] = Edge.App, ["Brain"] = "fixture-brain", ["ProgramRevision"] = "1" }, stop.Token, "fixture-hash", "fixture-revision");
                var entry = Assembly.Load(Scripts.Value[script].Assembly).EntryPoint!;
                executions.Add(Task.Run(async () =>
                {
                    try { if (entry.Invoke(null, [Array.Empty<string>()]) is Task task) { await task; } }
                    catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException && stop.IsCancellationRequested) { }
                    catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException failure) { throw failure; }
                }, CancellationToken.None));
            }
            return new(session, stop, Task.WhenAll(executions));
        }

        public async Task Wait(Func<bool> predicate)
        {
            await TestWait.UntilAsync(async _ =>
            {
                if (execution.IsCompleted) { await Complete(); }
                return predicate();
            }, done => done, Within, TestContext.Current.CancellationToken);
        }

        public async Task Complete()
        {
            try { await execution.WaitAsync(Within, TestContext.Current.CancellationToken); }
            catch (InvalidOperationException) { _failureObserved = true; throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            try { await execution; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (InvalidOperationException) when (_failureObserved) { }
            finally { await session.DisposeAsync(); stop.Dispose(); }
        }
    }

    private sealed class Edge : HttpMessageHandler
    {
        public const string App = "fixture-brain/apps/selfimprove";
        public const string CandidateKey = "fixture-brain/aspire/candidate/candidate-sha";
        private readonly Channel<byte[]> _events = Channel.CreateUnbounded<byte[]>();
        private readonly ConcurrentDictionary<string, string> _texts = new(StringComparer.Ordinal);
        private long _watermark;
        private int _initialized, _acknowledged;
        public volatile bool Available;
        public bool HasCandidate { get; init; }
        public volatile bool FailDiscovery;
        public bool FailDeployment { get; init; }
        public bool QueueProposals { get; init; }
        public volatile bool PauseCallbackDiscovery;
        public CandidateStage Stage { get; set; } = CandidateStage.Healthy;
        public CandidateStage? NextStage { get; set; }
        public ConcurrentDictionary<string, byte> UnavailableCallbacks { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource ResumeCallbacks { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<RuntimeInvocation> Calls { get; } = new();
        public ConcurrentDictionary<string, SubscribeReport> Subscriptions { get; } = new(StringComparer.Ordinal);
        public int Initialized => Volatile.Read(ref _initialized);
        public int Acknowledged => Volatile.Read(ref _acknowledged);
        public string Text(string part) => _texts.GetValueOrDefault(App + "/" + part, "");
        public void SetText(string part, string text) => _texts[App + "/" + part] = text;

        public Guid Request(string operation, string input, Guid? id = null)
        {
            var requestId = id ?? Guid.NewGuid();
            Publish(App, nameof(AppRequested), new AppRequested(operation, input, requestId));
            return requestId;
        }

        public void Changed(string source = "fixture-brain/aspire/candidates", string sha = "candidate-sha")
            => Publish(source, nameof(CandidateChanged), new CandidateChanged(sha, sha == "candidate-sha" ? Stage : NextStage!.Value));

        private void Publish<T>(string source, string signal, T value)
        {
            foreach (var (id, subscription) in Subscriptions.Where(item => item.Value.Key == source && item.Value.Signal == signal))
            {
                var batch = new SignalBatchEvent(id, [JsonSerializer.Serialize(value, RuntimeProtocol.Json)], Interlocked.Increment(ref _watermark));
                _events.Writer.TryWrite(Encoding.UTF8.GetBytes($"event: {SignalBatchEvent.Kind}\ndata: {JsonSerializer.Serialize(batch, RuntimeProtocol.Json)}\n\n"));
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(RuntimeProtocol.Stream, StringComparison.Ordinal))
            { return new(HttpStatusCode.OK) { Content = new StreamContent(new EventStream(_events.Reader)) { Headers = { ContentType = new("text/event-stream") } } }; }
            if (path.EndsWith(RuntimeProtocol.Report, StringComparison.Ordinal))
            {
                var report = (await request.Content!.ReadFromJsonAsync<RuntimeReport>(RuntimeProtocol.Json, cancellationToken))!;
                if (report.Status?.Status == "Initialized") { Interlocked.Increment(ref _initialized); }
                if (report.Acknowledge is not null || report.Acknowledgements is { Length: > 0 }) { Interlocked.Increment(ref _acknowledged); }
                if (report.Subscribe is not { } subscribed) { return new(HttpStatusCode.OK); }
                var id = subscribed.Behavior + "/" + subscribed.Key + "/" + subscribed.Signal;
                Subscriptions[id] = subscribed;
                return Json(new { subscription = id });
            }
            var call = (await request.Content!.ReadFromJsonAsync<RuntimeInvocation>(RuntimeProtocol.Json, cancellationToken))!;
            Calls.Enqueue(call);
            if (call.Contract == typeof(IConnectionRequests).FullName && call.Method == nameof(IConnectionRequests.ListKinds))
            {
                Assert.Equal("fixture-brain", call.Key);
                var callback = call.Behavior.EndsWith("/healthy.cs", StringComparison.Ordinal) || call.Behavior.EndsWith("/verified.cs", StringComparison.Ordinal);
                if (callback && !Available) { UnavailableCallbacks.TryAdd(call.Behavior, 0); }
                if (callback && PauseCallbackDiscovery) { await ResumeCallbacks.Task.WaitAsync(cancellationToken); }
                return FailDiscovery ? Failure("Discovery fixture failed") : Json<ConnectorKind[]>(Available ? [new("aspire", "Deployment")] : [new("other", "Other")]);
            }
            if (call.Contract == RuntimeProtocol.KernelContract && call.Method == RuntimeProtocol.KernelInstances)
            { return Json<Instance[]>(HasCandidate ? [new("aspire.candidate", "fixture-brain/aspire/candidate/candidate-sha", DateTimeOffset.UnixEpoch, null)] : []); }
            if (call.Contract == typeof(IText).FullName)
            {
                if (call.Method == nameof(IText.Read)) { return Json(new TextState { Name = call.Key, Markdown = _texts.GetValueOrDefault(call.Key, "") }); }
                if (call.Method == nameof(IText.Set)) { _texts[call.Key] = call.Arguments[0].GetString()!; return new(HttpStatusCode.NoContent); }
            }
            if (call.Contract == typeof(IDeployment).FullName)
            { return FailDeployment ? Failure("Host fixture failed") : Json(new DeploymentSnapshot("current-sha", DeploymentStage.Steady, null, [], null)); }
            if (call.Contract == typeof(ICandidates).FullName && call.Method == nameof(ICandidates.Propose))
            {
                if (QueueProposals)
                {
                    Assert.NotEqual(CandidateStage.Promoted, Stage);
                    NextStage = CandidateStage.Queued;
                    Changed(sha: "new-sha");
                }
                return Json("new-sha");
            }
            if (call.Contract == typeof(ICandidate).FullName)
            {
                var next = call.Key.EndsWith("/new-sha", StringComparison.Ordinal);
                var stage = next ? NextStage!.Value : Stage;
                if (call.Method == nameof(ICandidate.Verify)) { Assert.True(Available); stage = CandidateStage.Verified; }
                else if (call.Method == nameof(ICandidate.Promote)) { Assert.True(Available); stage = CandidateStage.Promoted; }
                else { Assert.Equal(nameof(ICandidate.Read), call.Method); }
                if (call.Method != nameof(ICandidate.Read))
                {
                    if (next) { NextStage = stage; }
                    else { Stage = stage; }
                    if (QueueProposals)
                    {
                        Changed(call.Key, next ? "new-sha" : "candidate-sha");
                        if (!next && stage == CandidateStage.Promoted && NextStage == CandidateStage.Queued)
                        {
                            NextStage = CandidateStage.Starting;
                            Changed();
                        }
                    }
                }
                return Json(new CandidateSnapshot(next ? "new-sha" : "candidate-sha", "fixture", "Existing change", stage, null, new([], [], TimeSpan.FromMinutes(1)), null, null, [CandidateStage.Starting, stage]));
            }
            if (call.Contract == typeof(ILayout).FullName)
            { return call.Method == nameof(ILayout.Read) ? Json(new LayoutState()) : new(HttpStatusCode.NoContent); }
            if (call.Contract == typeof(IApp).FullName && call.Method == nameof(IApp.Present)) { return new(HttpStatusCode.NoContent); }
            throw new InvalidOperationException($"Unexpected script call: {call.Contract}.{call.Method} ({call.Key}).");
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: RuntimeProtocol.Json) };
        private static HttpResponseMessage Failure(string detail) => new(HttpStatusCode.UnprocessableEntity) { Content = new StringContent(detail) };

        private sealed class EventStream(ChannelReader<byte[]> events) : Stream
        {
            private ReadOnlyMemory<byte> _remaining;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_remaining.IsEmpty) { _remaining = await events.ReadAsync(cancellationToken); }
                var length = Math.Min(buffer.Length, _remaining.Length);
                _remaining[..length].CopyTo(buffer);
                _remaining = _remaining[length..];
                return length;
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
