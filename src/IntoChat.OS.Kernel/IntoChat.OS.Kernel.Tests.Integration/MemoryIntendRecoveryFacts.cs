using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Agents.Intents;
using IntoChat.OS.AI.Agents.Signals;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Microsoft.CSharp.Catalog;

namespace IntoChat.OS.Kernel.Tests.Integration;

// Runs the shipped intend script and real HTTP client. The controlled edge can lose a reply before
// or after persisting a mutation, so recovery must read durable state rather than repeat a write blindly.
public sealed class MemoryIntendRecoveryFacts
{
    private static readonly Lazy<byte[]> Script = new(Compile);
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(nameof(IIntents.Rephrase), false)]
    [InlineData(nameof(IIntents.Rephrase), true)]
    [InlineData(nameof(IIntents.Transition), false)]
    [InlineData(nameof(IIntents.Transition), true)]
    public async Task ATransientMutationFailureReadsBackBeforeRetrying(string method, bool committed)
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new IntendEdge { FaultMethod = method, CommitBeforeFailure = committed };
        await using var running = await Running.Start(edge, ct);

        var answer = await edge.Answer.Task.WaitAsync(Within, ct);

        Assert.Equal("ok " + IntendEdge.IntentId, answer);
        Assert.Equal(IntentState.Confirmed, Assert.Single(edge.Snapshot.Intents).State);
        Assert.Equal(1, edge.Declarations);
        Assert.Equal(method == nameof(IIntents.Rephrase) && !committed ? 2 : 1, edge.Rephrases);
        Assert.Equal(method == nameof(IIntents.Transition) && !committed ? 2 : 1, edge.Transitions);
        Assert.True(edge.ReadsAfterFailure > 0);
    }

    [Fact]
    public async Task ALostReplyOnTheLastConfirmationIsAcceptedWithoutAFourthWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new IntendEdge { Failures = 3, CommitOnAttempt = 3 };
        await using var running = await Running.Start(edge, ct);

        Assert.Equal("ok " + IntendEdge.IntentId, await edge.Answer.Task.WaitAsync(Within, ct));
        Assert.Equal(IntentState.Confirmed, Assert.Single(edge.Snapshot.Intents).State);
        Assert.Equal(3, edge.Transitions);
        Assert.Equal(1, edge.Declarations);
    }

    [Fact]
    public async Task ALostReplyOnTheLastRephraseIsAcceptedAndConfirmed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new IntendEdge { FaultMethod = nameof(IIntents.Rephrase), Failures = 3, CommitOnAttempt = 3 };
        await using var running = await Running.Start(edge, ct);

        Assert.Equal("ok " + IntendEdge.IntentId, await edge.Answer.Task.WaitAsync(Within, ct));
        Assert.Equal(IntentState.Confirmed, Assert.Single(edge.Snapshot.Intents).State);
        Assert.Equal(3, edge.Rephrases);
        Assert.Equal(1, edge.Transitions);
        Assert.Equal(1, edge.Declarations);
    }

    [Fact]
    public async Task RepeatedTransientFailuresStopAfterThreeMutationAttempts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new IntendEdge { Failures = int.MaxValue };
        await using var running = await Running.Start(edge, ct);

        var answer = await edge.Answer.Task.WaitAsync(Within, ct);

        Assert.StartsWith("failed:", answer, StringComparison.Ordinal);
        Assert.Equal(IntentState.Rephrased, Assert.Single(edge.Snapshot.Intents).State);
        Assert.Equal(3, edge.Transitions);
        Assert.Equal(1, edge.Declarations);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ADefinitiveMutationRefusalIsNotRetried(HttpStatusCode status)
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new IntendEdge { FailureStatus = status, Failures = int.MaxValue };
        await using var running = await Running.Start(edge, ct);

        var answer = await edge.Answer.Task.WaitAsync(Within, ct);

        Assert.StartsWith("failed:", answer, StringComparison.Ordinal);
        Assert.Equal(1, edge.Transitions);
        Assert.Equal(1, edge.Declarations);
    }

    [Fact]
    public async Task MalformedModelOutputDoesNotDeclareOrRetryAnIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new IntendEdge { ModelReply = "not a brief" };
        await using var running = await Running.Start(edge, ct);

        var answer = await edge.Answer.Task.WaitAsync(Within, ct);

        Assert.StartsWith("failed:", answer, StringComparison.Ordinal);
        Assert.Empty(edge.Snapshot.Intents);
        Assert.Equal(0, edge.Declarations);
        Assert.Equal(0, edge.Transitions);
    }

    [Fact]
    public async Task RecoveryDoesNotRephraseAnIntentThatBecameTerminal()
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new IntendEdge { FaultMethod = nameof(IIntents.Rephrase), CommitBeforeFailure = true, ConcurrentTerminal = true };
        await using var running = await Running.Start(edge, ct);

        var answer = await edge.Answer.Task.WaitAsync(Within, ct);

        Assert.StartsWith("failed:", answer, StringComparison.Ordinal);
        Assert.Equal(IntentState.Done, Assert.Single(edge.Snapshot.Intents).State);
        Assert.Equal(1, edge.Rephrases);
        Assert.Equal(0, edge.Transitions);
        Assert.Equal(1, edge.Declarations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryPreservesAConcurrentChangeToTheBriefOrConfidence(bool confidenceOnly)
    {
        var ct = TestContext.Current.CancellationToken;
        using var edge = new IntendEdge
        {
            FaultMethod = nameof(IIntents.Rephrase),
            CommitBeforeFailure = true,
            ConcurrentBrief = !confidenceOnly,
            ConcurrentConfidence = confidenceOnly
        };
        await using var running = await Running.Start(edge, ct);

        Assert.StartsWith("failed:", await edge.Answer.Task.WaitAsync(Within, ct), StringComparison.Ordinal);
        var intent = Assert.Single(edge.Snapshot.Intents);
        Assert.Equal(confidenceOnly ? "Return ready" : "A newer goal", intent.Brief!.Goal);
        Assert.Equal(confidenceOnly ? 0.5 : 1, intent.Confidence);
        Assert.Equal(1, edge.Rephrases);
        Assert.Equal(0, edge.Transitions);
        Assert.Equal(1, edge.Declarations);
    }

    private static byte[] Compile()
    {
        var feature = new FeatureDirectory(SolutionProjects.Features).Load().Single(item => item.Owner == "os" && item.Bundle.Name == "memory");
        var sources = feature.Bundle.Files.Where(file => AppFiles.IsProgram(file.Key)
            && (file.Key == "intend.cs" || CSharpScriptCheck.IsShared(file.Value)))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
        var compiled = new CSharpScriptCheck().Compile(sources);
        Assert.True(compiled.Success, string.Join("\n", compiled.Errors));
        return compiled.Assemblies["intend.cs"].Assembly;
    }

    private sealed class Running(SessionConnection session, CancellationTokenSource stop, Task execution) : IAsyncDisposable
    {
        public static async Task<Running> Start(IntendEdge edge, CancellationToken ct)
        {
            var session = await SessionConnection.OpenAsync(new("http://brain.test/brains/intend-brain/"), "fixture-token", "fixture-fingerprint", edge, ct);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var ambient = AmbientSession.Enter(session, IntendEdge.App + "/intend.cs", new Dictionary<string, string>
            { ["App"] = IntendEdge.App, ["Brain"] = "intend-brain" }, stop.Token);
            var entry = Assembly.Load(Script.Value).EntryPoint!;
            var execution = Task.Run(async () =>
            {
                try { if (entry.Invoke(null, [Array.Empty<string>()]) is Task task) { await task; } }
                catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException && stop.IsCancellationRequested) { }
            }, CancellationToken.None);
            return new(session, stop, execution);
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            try { await execution; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            await session.DisposeAsync();
            stop.Dispose();
        }
    }

    private sealed class IntendEdge : HttpMessageHandler
    {
        public const string App = "intend-brain/apps/memory";
        public const string RequestId = "00000000000000000000000000000001";
        public static readonly Guid IntentId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        private readonly Lock _gate = new();
        private readonly Channel<byte[]> _events = Channel.CreateUnbounded<byte[]>();
        private IntentsSnapshot _snapshot = new(0, []);
        private bool _failed;
        private int _declarations, _rephrases, _transitions, _readsAfterFailure;
        public string FaultMethod { get; init; } = nameof(IIntents.Transition);
        public bool CommitBeforeFailure { get; init; }
        public int CommitOnAttempt { get; init; }
        public bool ConcurrentTerminal { get; init; }
        public bool ConcurrentBrief { get; init; }
        public bool ConcurrentConfidence { get; init; }
        public int Failures { get; init; } = 1;
        public HttpStatusCode FailureStatus { get; init; } = HttpStatusCode.GatewayTimeout;
        public string ModelReply { get; init; } = """{"brief":{"goal":"Return ready","inputs":[],"constraints":[],"acceptance":["Reply ready"]},"confidence":1,"continues":null}""";
        public TaskCompletionSource<string> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IntentsSnapshot Snapshot { get { lock (_gate) { return _snapshot; } } }
        public int Declarations => Volatile.Read(ref _declarations);
        public int Rephrases => Volatile.Read(ref _rephrases);
        public int Transitions => Volatile.Read(ref _transitions);
        public int ReadsAfterFailure => Volatile.Read(ref _readsAfterFailure);

        private void Publish<T>(string signal, T value)
        {
            var batch = new SignalBatchEvent(signal, [JsonSerializer.Serialize(value, RuntimeProtocol.Json)], 1);
            _events.Writer.TryWrite(Encoding.UTF8.GetBytes($"event: {SignalBatchEvent.Kind}\ndata: {JsonSerializer.Serialize(batch, RuntimeProtocol.Json)}\n\n"));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(RuntimeProtocol.Stream, StringComparison.Ordinal))
            { return new(HttpStatusCode.OK) { Content = new StreamContent(new EventStream(_events.Reader)) { Headers = { ContentType = new("text/event-stream") } } }; }
            if (path.EndsWith(RuntimeProtocol.Report, StringComparison.Ordinal))
            {
                var report = await request.Content!.ReadFromJsonAsync<RuntimeReport>(RuntimeProtocol.Json, cancellationToken);
                if (report?.Subscribe is not { } subscribed) { return new(HttpStatusCode.OK); }
                if (subscribed.Signal == nameof(AppRequested))
                {
                    var input = JsonSerializer.Serialize(new { message = "Reply ready", requestId = RequestId, conversation = "main" });
                    Publish(nameof(AppRequested), new AppRequested("intend", input, Guid.Parse(RequestId)));
                }
                return Json(new { subscription = subscribed.Signal });
            }
            var call = (await request.Content!.ReadFromJsonAsync<RuntimeInvocation>(RuntimeProtocol.Json, cancellationToken))!;
            if (call.Contract == typeof(IApp).FullName && call.Method == nameof(IApp.Read))
            { return Json(new AppSnapshot(App, new("Memory", "", [], [], []), "", [], [], [], "1.2.0", AppStatus.Live, [], [])); }
            if (call.Contract == typeof(IText).FullName)
            {
                if (call.Method == nameof(IText.Read)) { return Json(new TextState { Name = call.Key }); }
                if (call.Method == nameof(IText.Set) && call.Key == App + "/intents/" + RequestId)
                { Answer.TrySetResult(call.Arguments[0].GetString()!); return new(HttpStatusCode.NoContent); }
            }
            if (call.Contract == typeof(IAgent).FullName && call.Method == nameof(IAgent.Start))
            {
                var turn = call.Arguments[0].Deserialize<AgentTurn>(RuntimeProtocol.Json)!;
                Publish(nameof(TurnCompleted), new TurnCompleted(call.Key, turn.RunId!, ModelReply));
                return new(HttpStatusCode.NoContent);
            }
            if (call.Contract == typeof(IIntents).FullName)
            {
                lock (_gate)
                {
                    if (call.Method == nameof(IIntents.Read))
                    { if (_failed) { Interlocked.Increment(ref _readsAfterFailure); } return Json(_snapshot); }
                    if (call.Method == nameof(IIntents.Declare))
                    {
                        Interlocked.Increment(ref _declarations);
                        var draft = call.Arguments[0].Deserialize<IntentDraft>(RuntimeProtocol.Json)!;
                        Assert.Equal(RequestId, draft.RequestId);
                        var intent = new Intent(IntentId, draft.Heard, draft.Conversation, draft.RequestId, null, 0, IntentState.Heard, [], [], []);
                        _snapshot = new(1, [intent]);
                        return Json(IntentId);
                    }
                    var current = Assert.Single(_snapshot.Intents);
                    var rephrase = call.Method == nameof(IIntents.Rephrase);
                    var attempt = rephrase ? Interlocked.Increment(ref _rephrases) : Interlocked.Increment(ref _transitions);
                    Assert.Equal(rephrase ? nameof(IIntents.Rephrase) : nameof(IIntents.Transition), call.Method);
                    Assert.Equal(IntentId, call.Arguments[0].GetGuid());
                    Assert.Equal(_snapshot.Revision, call.Arguments[3].GetInt64());
                    if (current.State is IntentState.Done or IntentState.Abandoned)
                    { return new(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("Intent is terminal") }; }
                    var fail = call.Method == FaultMethod && attempt <= Failures;
                    if (!fail || CommitBeforeFailure || attempt == CommitOnAttempt)
                    {
                        var next = rephrase ? current with
                        {
                            Brief = call.Arguments[1].Deserialize<IntentBrief>(RuntimeProtocol.Json),
                            Confidence = call.Arguments[2].GetDouble(),
                            State = IntentState.Rephrased
                        } : current with { State = call.Arguments[1].Deserialize<IntentState>(RuntimeProtocol.Json) };
                        if (fail && ConcurrentTerminal) { next = next with { State = IntentState.Done }; }
                        if (fail && ConcurrentBrief) { next = next with { Brief = next.Brief! with { Goal = "A newer goal" } }; }
                        if (fail && ConcurrentConfidence) { next = next with { Confidence = 0.5 }; }
                        _snapshot = new(_snapshot.Revision + 1, [next]);
                    }
                    if (fail) { _failed = true; return new(FailureStatus) { Content = new StringContent("Gateway response unavailable") }; }
                    return new(HttpStatusCode.NoContent);
                }
            }
            throw new InvalidOperationException($"Unexpected script call: {call.Contract}.{call.Method} ({call.Key}).");
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: RuntimeProtocol.Json) };

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
