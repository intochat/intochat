using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using IntoChat.OS;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Metering;
using IntoChat.OS.Framework.Compute;
using Microsoft.Extensions.AI;
using Xunit;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

public sealed class MeteringFacts(AIHost host)
{
    [Fact]
    public async Task AChatResponseRecordsInputAndOutputCosts()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter();
        var usage = new UsageDetails
        {
            InputTokenCount = 10,
            CachedInputTokenCount = 3,
            ReasoningTokenCount = 2,
            OutputTokenCount = 5,
            TotalTokenCount = 15,
        };
        var client = new MeteringChatClient(FixedChatClient(Response(usage, "gpt-5.6-luna")), meter, Funding.Integration, "gpt-5.6-luna");

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: ct);

        Assert.Equal(4, meter.Calls.Count);
        var input = Assert.Single(meter.Calls, call => call.CostId.EndsWith(":input", StringComparison.Ordinal));
        Assert.Equal(CostKind.Model, input.Kind);
        Assert.Equal("gpt-5.6-luna:input", input.Item);
        Assert.Equal(10 / 1_000_000m, input.Quantity);
        Assert.Equal("mtok", input.Unit);
        Assert.Equal(Funding.Integration, input.Funding);
        Assert.Equal($"{response.ResponseId}:input", input.CostId);
        var output = Assert.Single(meter.Calls, call => call.CostId.EndsWith(":output", StringComparison.Ordinal));
        Assert.Equal(5 / 1_000_000m, output.Quantity);
        Assert.Equal($"{response.ResponseId}:output", output.CostId);
        Assert.Equal(3 / 1_000_000m, Assert.Single(meter.Calls, call => call.CostId.EndsWith(":cached", StringComparison.Ordinal)).Quantity);
        Assert.Equal(2 / 1_000_000m, Assert.Single(meter.Calls, call => call.CostId.EndsWith(":reasoning", StringComparison.Ordinal)).Quantity);
    }

    [Fact]
    public async Task TheTokenClassesGoToTheMeterTogetherNotOneAfterAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        // Every row waits until all four are in flight: a meter fed one class after another never gets past the first.
        var meter = new BarrierMeter(4);
        var usage = new UsageDetails { InputTokenCount = 10, CachedInputTokenCount = 3, ReasoningTokenCount = 2, OutputTokenCount = 5 };
        var client = new MeteringChatClient(FixedChatClient(Response(usage, "gpt-5.6-luna")), meter, Funding.Integration, "gpt-5.6-luna");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: ct);

        Assert.Equal(["cached", "input", "output", "reasoning"], meter.Recorded.Select(costId => costId.Split(':')[^1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task StreamingUsageIsRecordedOnceFromTheFinalChunk()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter();
        var usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 4, TotalTokenCount = 11 };
        var client = new MeteringChatClient(StreamingChatClient(usage), meter, Funding.Integration, "claude");

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: ct)) { }

        var input = Assert.Single(meter.Calls, call => call.CostId.EndsWith(":input", StringComparison.Ordinal));
        Assert.Equal("claude:input", input.Item);
        Assert.Equal(7 / 1_000_000m, input.Quantity);
        var output = Assert.Single(meter.Calls, call => call.CostId.EndsWith(":output", StringComparison.Ordinal));
        Assert.Equal(4 / 1_000_000m, output.Quantity);
    }

    [Fact]
    public async Task AResponseWithoutReportedUsageRecordsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter();
        var client = new MeteringChatClient(FixedChatClient(Response(null, "gpt-5.6-luna")), meter, Funding.Integration, "gpt-5.6-luna");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: ct);

        Assert.Empty(meter.Calls);
    }

    [Fact]
    public async Task AStreamThatEndsWithoutUsageRecordsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter();
        var client = new MeteringChatClient(StreamingChatClient(null), meter, Funding.Integration, "claude");

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: ct)) { }

        Assert.Empty(meter.Calls);
    }

    [Fact]
    public async Task AnEmbeddingCallRecordsItsInputTokensAsALocalCost()
    {
        var ct = TestContext.Current.CancellationToken;
        var meter = new RecordingMeter();
        var generator = new MeteringEmbeddingGenerator(new FixedEmbeddingGenerator(new UsageDetails { InputTokenCount = 9, TotalTokenCount = 9 }), meter, Funding.Local, "text-embedding-3-small");

        await generator.GenerateAsync(["one", "two"], cancellationToken: ct);

        var call = Assert.Single(meter.Calls);
        Assert.Equal(CostKind.Model, call.Kind);
        Assert.Equal("text-embedding-3-small:input", call.Item);
        Assert.Equal(9 / 1_000_000m, call.Quantity);
        Assert.Equal("mtok", call.Unit);
        Assert.Equal(Funding.Local, call.Funding);
    }

    [Fact]
    public async Task UsageSurvivesGrainReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var usage = brain.Get<IIntentUsage>("intent-durable");
        await usage.RecordAsync(new TokenUsageEntry(MeterKind.Chat, "OpenAI", "gpt-5.6-luna", 10, 3, 2, 5, 15, true, DateTimeOffset.UnixEpoch), ct);

        await brain.DeactivateAsync(usage, ct);

        var read = await usage.ReadAsync(ct);
        Assert.Equal("intent-durable", read.IntentId);
        var entry = Assert.Single(read.Entries);
        Assert.Equal(10, entry.InputTokens);
        Assert.Equal(3, entry.CachedInputTokens);
        Assert.Equal(2, entry.ReasoningTokens);
        Assert.Equal(5, entry.OutputTokens);
        Assert.Equal(15, entry.TotalTokens);
    }

    private static ChatResponse Response(UsageDetails? usage, string modelId)
        => new(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = usage, ModelId = modelId, ResponseId = "response-1" };

    private sealed record RecordedCost(CostKind Kind, string Item, decimal Quantity, string Unit, Funding Funding, string CostId);

    private sealed class RecordingMeter : ICostMeter
    {
        public ValueTask<ExternalCallHandle> BeginExternalCallAsync(ExternalCallRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask MarkExternalCallDispatchStartedAsync(ExternalCallHandle handle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ExternalCallCompletion> CompleteExternalCallAsync(ExternalCallHandle handle, ExternalCallOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ConcurrentQueue<RecordedCost> Calls { get; } = new();

        public ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
            string costId, CancellationToken cancellationToken, CostSubject? subject = null)
        {
            Calls.Enqueue(new RecordedCost(kind, item, quantity, unit, funding, costId));
            return ValueTask.FromResult(new Cost
            {
                TraceId = TraceContext.Current?.TraceId ?? string.Empty,
                CostId = costId,
                Kind = kind,
                Item = item,
                Quantity = quantity,
                Unit = unit,
                Micros = 0,
            });
        }
    }

    private sealed class BarrierMeter(int rows) : ICostMeter
    {
        public ValueTask<ExternalCallHandle> BeginExternalCallAsync(ExternalCallRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask MarkExternalCallDispatchStartedAsync(ExternalCallHandle handle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ExternalCallCompletion> CompleteExternalCallAsync(ExternalCallHandle handle, ExternalCallOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public ConcurrentQueue<string> Recorded { get; } = new();

        public async ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
            string costId, CancellationToken cancellationToken, CostSubject? subject = null)
        {
            if (Interlocked.Increment(ref _started) == rows) { _all.TrySetResult(); }
            await _all.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Recorded.Enqueue(costId);
            return new Cost { TraceId = string.Empty, CostId = costId, Kind = kind, Item = item, Quantity = quantity, Unit = unit, Micros = 0 };
        }
    }

    private static StubChatClient FixedChatClient(ChatResponse response) => new(
        (_, _, _) => Task.FromResult(response),
        (_, _, _) => Updates(new ChatResponseUpdate(ChatRole.Assistant, "ok")));

    private static StubChatClient StreamingChatClient(UsageDetails? usage) => StubChatClient.StreamingOnly((_, _, _) => Updates(
        usage is null
            ? [new ChatResponseUpdate(ChatRole.Assistant, "par")]
            : [new ChatResponseUpdate(ChatRole.Assistant, "par"), new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)])]));

    private static async IAsyncEnumerable<ChatResponseUpdate> Updates(params ChatResponseUpdate[] updates)
    {
        await Task.CompletedTask;
        foreach (var update in updates) { yield return update; }
    }

    private sealed class FixedEmbeddingGenerator(UsageDetails usage) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(value => new Embedding<float>(new float[] { value.Length, 1 }))) { Usage = usage });

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }
}
