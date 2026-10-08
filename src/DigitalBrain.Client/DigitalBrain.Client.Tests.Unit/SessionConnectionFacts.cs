using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;

namespace DigitalBrain.Client.Tests.Unit;

public sealed class SessionConnectionFacts
{
    public sealed record Changed(int Value) : Signal;
    public interface ISource : INeuron { Task<int> Double(int value); Task Refuse(); }

    [Fact(Timeout = 30_000)]
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

    [Fact(Timeout = 30_000)]
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
        Assert.Equal(nameof(Changed), subscribe["signal"]!.GetValue<string>());
        Assert.Equal(2, (await brain.Acknowledged.Reader.ReadAsync(ct))["watermark"]!.GetValue<long>());
    }

    [Fact(Timeout = 30_000)]
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
    [Fact(Timeout = 30_000)]
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

    [Fact(Timeout = 30_000)]
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

    [Theory(Timeout = 30_000)]
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

    [Fact(Timeout = 30_000)]
    public async Task ARefusedFingerprintFailsTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new FakeBrain { RefuseFingerprint = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => brain.OpenAsync(ct));
    }

    [Fact(Timeout = 30_000)]
    public async Task CapacityRefusalTravelsThroughTheClientVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new FakeBrain();
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, "acc-brain/csharp/a", ct);

        var error = await Assert.ThrowsAsync<ScriptRefusalException>(() => connection.Get<ISource>("source").Refuse());

        Assert.Equal(("The brain is at capacity.", "CapacityUnavailableException"), (error.Message, error.Code));
    }

    [Fact(Timeout = 30_000)]
    public async Task ConnectAsyncOutsideASessionThrows()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DigitalBrainClient.ConnectAsync([], TestContext.Current.CancellationToken));
        Assert.Contains("sandbox session", error.Message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConnectAsyncInsideASessionReadsTheBehaviorsSettings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await new FakeBrain().OpenAsync(ct);
        using (AmbientSession.Enter(session, "acc-brain/csharp/a", new Dictionary<string, string> { ["TimerId"] = "tea" }, ct))
        {
            await using var connection = await DigitalBrainClient.ConnectAsync([], ct);
            Assert.Equal("tea", connection.Setting("TimerId"));
            Assert.Null(connection.Setting("Missing"));
        }
    }

    private static async Task<DigitalBrainConnection> Connect(SessionConnection session, string behavior, CancellationToken ct)
    {
        using (AmbientSession.Enter(session, behavior, new Dictionary<string, string>(), ct))
        { return await DigitalBrainClient.ConnectAsync([], ct); }
    }

    private static string Batch(string subscription, long watermark, params int[] values) => Batch(subscription, watermark, null, values);

    private static string Batch(string subscription, long watermark, string? trace, params int[] values)
        => $"event: {SignalBatchEvent.Kind}\ndata: {JsonSerializer.Serialize(new SignalBatchEvent(subscription, [.. values.Select(value => JsonSerializer.Serialize(new { value }))], watermark, trace), RuntimeProtocol.Json)}\n\n";

    // Speaks the brain side of the three routes. Each GET of the stream serves the next scripted body
    // after a ready event, then ends; once the script runs out, a stream stays open until cancelled.
    private sealed class FakeBrain : HttpMessageHandler
    {
        public List<string> Streams { get; } = [];
        public bool RefuseFingerprint { get; init; }
        public ConcurrentQueue<(string Path, string? Authorization, JsonNode? Body, string? TraceParent)> Requests { get; } = new();
        public System.Threading.Channels.Channel<JsonNode> Acknowledged { get; } = System.Threading.Channels.Channel.CreateUnbounded<JsonNode>();
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
            if (body?["acknowledge"] is { } acknowledge) { Acknowledged.Writer.TryWrite(acknowledge.DeepClone()); }
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
