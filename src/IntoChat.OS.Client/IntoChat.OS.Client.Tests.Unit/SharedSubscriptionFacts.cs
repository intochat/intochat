using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Client.Runtime.V1;
using static IntoChat.OS.Client.Tests.Unit.SessionConnectionFacts;

namespace IntoChat.OS.Client.Tests.Unit;

// The brain keeps one durable subscription per behavior, neuron and signal type, so two readers of the same
// neuron's signal in one behavior share its id: each reader is still handed every batch, and the brain's
// watermark only moves past a batch once every reader has asked for the next one.
public sealed class SharedSubscriptionFacts
{
    [Fact]
    public async Task TwoReadersOfOneNeuronsSignalInOneBehaviorEachGetEveryBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new SharedBrain(Batch("sub-source", 1, 1));
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, ct);
        var secondSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = connection.On<Changed>(connection.Get<ISource>("source"), ct).GetAsyncEnumerator(ct);
        await using var second = connection.On<Changed>(connection.Get<ISource>("source"), Subscribed(secondSubscribed), ct).GetAsyncEnumerator(ct);

        Assert.True(await first.MoveNextAsync());
        var firstOne = first.Current.Value;
        var secondHasOne = second.MoveNextAsync().AsTask();
        await secondSubscribed.Task.WaitAsync(ct);
        // The first reader asks for more while the second reads: one reader's wait must not end the other's.
        var firstHasTwo = first.MoveNextAsync().AsTask();
        Assert.Same(secondHasOne, await Task.WhenAny(secondHasOne, firstHasTwo));
        Assert.True(await secondHasOne);
        Assert.Equal((1, 1), (firstOne, second.Current.Value));

        await brain.PushAsync(Batch("sub-source", 2, 1, 2), ct);

        Assert.True(await firstHasTwo);
        Assert.True(await second.MoveNextAsync());
        Assert.Equal((2, 2), (first.Current.Value, second.Current.Value));
        Assert.Equal(2, brain.Subscribed.Count(key => key == "source"));
    }

    // A filter that passes over every signal of a batch (an app operation's synapse seeing another operation)
    // acknowledges it as skipped; a batch it hands on is a run.
    [Fact]
    public async Task ABatchEverySignalOfWhichWasPassedOverIsAcknowledgedAsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new SharedBrain(Batch("sub-source", 1, 1));
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, ct);
        await using var reader = connection.On<Changed>(connection.Get<ISource>("source"), ct).GetAsyncEnumerator(ct);

        Assert.True(await reader.MoveNextAsync());
        SignalDeliveries.Skip(reader.Current);
        var hasTwo = reader.MoveNextAsync().AsTask();
        var skipped = await brain.Acknowledged.Reader.ReadAsync(ct);
        await brain.PushAsync(Batch("sub-source", 2, 1, 2), ct);
        Assert.True(await hasTwo);
        var hasThree = reader.MoveNextAsync().AsTask();
        var handled = await brain.Acknowledged.Reader.ReadAsync(ct);

        Assert.Equal((1L, true), (skipped["watermark"]!.GetValue<long>(), skipped["skipped"]!.GetValue<bool>()));
        Assert.Equal((2L, false), (handled["watermark"]!.GetValue<long>(), handled["skipped"]!.GetValue<bool>()));
        GC.KeepAlive(hasThree);
    }

    // The real seam: a synapse on one operation of its app reads every AppRequested of the app, and the request
    // for the other operation is acknowledged as skipped while its own is acknowledged as handled.
    [Fact]
    public async Task ARequestForAnotherOperationReadThroughTheOperationFilterIsAcknowledgedAsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new SharedBrain(Requested("sub-acc-brain/apps/x", 1, "close") + Requested("sub-acc-brain/apps/x", 2, "open"));
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = connection.On(connection.Get<IApp>("acc-brain/apps/x"), "open", stop.Token).GetAsyncEnumerator(stop.Token);

        var first = await reader.MoveNextAsync();
        var operation = reader.Current.Operation;
        var skipped = await brain.Acknowledged.Reader.ReadAsync(ct);
        var next = reader.MoveNextAsync().AsTask();
        var handled = await brain.Acknowledged.Reader.ReadAsync(ct);
        await stop.CancelAsync();
        await Record.ExceptionAsync(() => next);
        await reader.DisposeAsync();

        Assert.Equal((true, "open"), (first, operation));
        Assert.Equal((1L, true), (skipped["watermark"]!.GetValue<long>(), skipped["skipped"]!.GetValue<bool>()));
        Assert.Equal((2L, false), (handled["watermark"]!.GetValue<long>(), handled["skipped"]!.GetValue<bool>()));
    }

    // Two readers, one behind: the batch the ahead reader handled is not reported as skipped because the
    // behind reader's report lands later, and a skipped batch is not reported as handled because of a later one.
    [Fact]
    public async Task EachReportSaysWhetherItsOwnBatchesWereHandled()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new SharedBrain(Batch("sub-source", 1, 1));
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, ct);
        var behindSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ahead = connection.On<Changed>(connection.Get<ISource>("source"), ct).GetAsyncEnumerator(ct);
        await using var behind = connection.On<Changed>(connection.Get<ISource>("source"), Subscribed(behindSubscribed), ct).GetAsyncEnumerator(ct);

        Assert.True(await ahead.MoveNextAsync());
        SignalDeliveries.Skip(ahead.Current);
        var behindHasOne = behind.MoveNextAsync().AsTask();
        await behindSubscribed.Task.WaitAsync(ct);
        var aheadHasTwo = ahead.MoveNextAsync().AsTask();
        Assert.True(await behindHasOne);
        SignalDeliveries.Skip(behind.Current);
        await brain.PushAsync(Batch("sub-source", 2, 1, 2), ct);
        Assert.True(await aheadHasTwo);
        // The ahead reader handles batch 2 before the behind reader has moved past batch 1.
        var aheadWaits = ahead.MoveNextAsync().AsTask();
        Assert.True(await behind.MoveNextAsync());
        var first = await brain.Acknowledged.Reader.ReadAsync(ct);
        var behindWaits = behind.MoveNextAsync().AsTask();
        var second = await brain.Acknowledged.Reader.ReadAsync(ct);

        Assert.Equal((1L, true), (first["watermark"]!.GetValue<long>(), first["skipped"]!.GetValue<bool>()));
        Assert.Equal((2L, false), (second["watermark"]!.GetValue<long>(), second["skipped"]!.GetValue<bool>()));
        GC.KeepAlive((aheadWaits, behindWaits));
    }

    // A handled ack for a batch an earlier report already covered arrives late: it must not make the next
    // report, whose batches no reader handled, read as a run.
    [Fact]
    public async Task ALateHandledAckForAReportedBatchDoesNotTurnALaterSkipIntoARun()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new SharedBrain("");
        await using var session = await brain.OpenAsync(ct);
        var subscription = new SharedSubscription(session, "sub-late");
        var reader = subscription.Join();
        subscription.Arrive(new SignalBatchEvent("sub-late", ["{}", "{}"], 2));
        await reader.AcknowledgeAsync(2, 0, 0, skipped: true, ct);
        var first = await brain.Acknowledged.Reader.ReadAsync(ct);

        await reader.AcknowledgeAsync(1, 0, 0, skipped: false, ct);
        subscription.Arrive(new SignalBatchEvent("sub-late", ["{}"], 3));
        await reader.AcknowledgeAsync(3, 0, 0, skipped: true, ct);
        var second = await brain.Acknowledged.Reader.ReadAsync(ct);

        Assert.Equal((2L, true), (first["watermark"]!.GetValue<long>(), first["skipped"]!.GetValue<bool>()));
        Assert.Equal((3L, true), (second["watermark"]!.GetValue<long>(), second["skipped"]!.GetValue<bool>()));
    }

    private static string Requested(string subscription, long watermark, string operation)
        => $"event: {SignalBatchEvent.Kind}\ndata: {JsonSerializer.Serialize(new SignalBatchEvent(subscription, [JsonSerializer.Serialize(new AppRequested(operation, "", Guid.NewGuid()), RuntimeProtocol.Json)], watermark, null), RuntimeProtocol.Json)}\n\n";

    [Fact]
    public async Task TheWatermarkAdvancesOnlyOnceEveryReaderHasAcknowledged()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new SharedBrain(Batch("sub-source", 1, 1));
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, ct);
        var behindSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ahead = connection.On<Changed>(connection.Get<ISource>("source"), ct).GetAsyncEnumerator(ct);
        await using var behind = connection.On<Changed>(connection.Get<ISource>("source"), Subscribed(behindSubscribed), ct).GetAsyncEnumerator(ct);
        Assert.True(await ahead.MoveNextAsync());
        var behindHasOne = behind.MoveNextAsync().AsTask();
        await behindSubscribed.Task.WaitAsync(ct);

        // Asking for the next batch acknowledges the current one; a reader whose ack is reported waits for the report.
        var aheadHasTwo = ahead.MoveNextAsync().AsTask();
        Assert.Same(behindHasOne, await Task.WhenAny(behindHasOne, aheadHasTwo));
        Assert.True(await behindHasOne);
        await brain.PushAsync(Batch("sub-source", 2, 1, 2), ct);
        Assert.True(await aheadHasTwo);
        Assert.Equal(2, ahead.Current.Value);
        Assert.False(brain.Acknowledged.Reader.TryRead(out _));

        Assert.True(await behind.MoveNextAsync());
        Assert.Equal(2, behind.Current.Value);
        Assert.True(brain.Acknowledged.Reader.TryRead(out var acknowledged));
        Assert.Equal(("sub-source", 1L), (acknowledged["subscription"]!.GetValue<string>(), acknowledged["watermark"]!.GetValue<long>()));
        Assert.Equal(1, acknowledged["batches"]!.GetValue<int>());
        Assert.False(brain.Acknowledged.Reader.TryRead(out _));

        var aheadWaits = ahead.MoveNextAsync().AsTask();
        var behindWaits = behind.MoveNextAsync().AsTask();
        var next = await brain.Acknowledged.Reader.ReadAsync(ct);
        Assert.Equal(2, next["watermark"]!.GetValue<long>());
        Assert.False(aheadWaits.IsCompleted || behindWaits.IsCompleted);
    }

    // A reader that leaves does not hold the others back: once the slower reader is gone, what the rest
    // acknowledged reaches the brain.
    [Fact]
    public async Task AReaderThatLeavesNoLongerHoldsTheWatermark()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new SharedBrain(Batch("sub-source", 1, 1));
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, ct);
        var leavingSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var staying = connection.On<Changed>(connection.Get<ISource>("source"), ct).GetAsyncEnumerator(ct);
        await using var leaving = connection.On<Changed>(connection.Get<ISource>("source"), Subscribed(leavingSubscribed), ct).GetAsyncEnumerator(ct);
        Assert.True(await staying.MoveNextAsync());
        var leavingHasOne = leaving.MoveNextAsync().AsTask();
        await leavingSubscribed.Task.WaitAsync(ct);

        var stayingWaits = staying.MoveNextAsync().AsTask();
        Assert.Same(leavingHasOne, await Task.WhenAny(leavingHasOne, stayingWaits));
        Assert.True(await leavingHasOne);
        Assert.False(brain.Acknowledged.Reader.TryRead(out _));
        await leaving.DisposeAsync();

        var acknowledged = await brain.Acknowledged.Reader.ReadAsync(ct);
        Assert.Equal(1, acknowledged["watermark"]!.GetValue<long>());
        Assert.False(stayingWaits.IsCompleted);
    }

    // The shape that died live: a set named by a registry that is also one of its members reads the
    // registry's signal twice in one behavior, once to re-select and once as a member.
    [Fact]
    public async Task ASetWhoseRegistryIsAlsoAMemberYieldsItsSignalAndReselects()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new SharedBrain(Batch("sub-registry", 1, 1) + Batch("sub-member", 1, 2));
        await using var session = await brain.OpenAsync(ct);
        await using var connection = await Connect(session, ct);
        var registry = connection.Get<ISource>("registry");
        var member = connection.Get<ISource>("member");
        var reselected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selections = 0;
        var sources = Sources.Named<ISource, Changed, ISource>(registry, (_, _) =>
        {
            if (Interlocked.Increment(ref selections) == 2) { reselected.TrySetResult(); }
            return Task.FromResult<ISource[]>([registry, member]);
        });
        await using var stream = connection.On<Changed>(sources, ct).GetAsyncEnumerator(ct);

        var seen = new List<int>();
        Assert.True(await stream.MoveNextAsync());
        seen.Add(stream.Current.Value);
        Assert.True(await stream.MoveNextAsync());
        seen.Add(stream.Current.Value);
        var pending = stream.MoveNextAsync().AsTask();

        Assert.Equal([1, 2], seen.Order());
        Assert.Same(reselected.Task, await Task.WhenAny(reselected.Task, pending));
        Assert.Equal(2, brain.Subscribed.Count(key => key == "registry"));
    }

    private static Func<Task> Subscribed(TaskCompletionSource subscribed) => () => { subscribed.TrySetResult(); return Task.CompletedTask; };

    private static async Task<IntoChatOSConnection> Connect(SessionConnection session, CancellationToken ct)
    {
        using (AmbientSession.Enter(session, "acc-brain/csharp/a", new Dictionary<string, string>(), ct))
        { return await IntoChatOSClient.ConnectAsync([], ct); }
    }

    private static string Batch(string subscription, long watermark, params int[] values)
        => $"event: {SignalBatchEvent.Kind}\ndata: {JsonSerializer.Serialize(new SignalBatchEvent(subscription, [.. values.Select(value => JsonSerializer.Serialize(new { value }))], watermark), RuntimeProtocol.Json)}\n\n";

    // Answers every subscribe to a neuron with the same id, as the brain does for one behavior, neuron and
    // signal type. Its one stream stays open: it starts with the given events and carries each push.
    private sealed class SharedBrain(string head) : HttpMessageHandler
    {
        private readonly Pipe _stream = new();

        public ConcurrentQueue<string> Subscribed { get; } = new();
        public Channel<JsonNode> Acknowledged { get; } = Channel.CreateUnbounded<JsonNode>();

        public Task<SessionConnection> OpenAsync(CancellationToken ct)
            => SessionConnection.OpenAsync(new Uri("https://brain.test/brains/acc-brain/"), "token-1", ContractsFingerprint.Of([typeof(IBrain).Assembly]), this, ct);

        public async Task PushAsync(string events, CancellationToken ct) => await _stream.Writer.WriteAsync(Encoding.UTF8.GetBytes(events), ct);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var body = text.Length == 0 ? null : JsonNode.Parse(text);
            if (path.EndsWith(RuntimeProtocol.Stream, StringComparison.Ordinal))
            {
                await PushAsync($"event: {ReadyEvent.Kind}\ndata: {{\"session\":\"s\"}}\n\n" + head, cancellationToken);
                return new(HttpStatusCode.OK) { Content = new StreamContent(_stream.Reader.AsStream()) { Headers = { ContentType = new("text/event-stream") } } };
            }
            if (body?["subscribe"] is { } subscribe)
            {
                var key = subscribe["key"]!.GetValue<string>();
                Subscribed.Enqueue(key);
                return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"subscription\":\"sub-{key}\"}}") };
            }
            if (body?["acknowledgements"] is JsonArray acknowledgements)
            { foreach (var acknowledgement in acknowledgements) { Acknowledged.Writer.TryWrite(acknowledgement!.DeepClone()); } }
            return new(HttpStatusCode.OK);
        }
    }
}
