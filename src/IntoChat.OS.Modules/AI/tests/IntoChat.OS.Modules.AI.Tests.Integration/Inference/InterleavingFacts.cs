using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Signals;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

public sealed class InterleavingFacts
{
    private static readonly string Model = ModelAddress.Key("owner");

    [Trait("host", "own")]
    [Fact]
    public async Task TwoGenerationsOfOneBrainRunConcurrently()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new Gate(2);
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithChatModel(gate.Client).StartAsync(ct);
        var llm = brain.Get<ILLM>(Model);

        var answers = await Task.WhenAll(llm.Generate(Ask("one"), ct), llm.Generate(Ask("two"), ct));

        Assert.Equal(2, gate.Overlap);
        Assert.All(answers, answer => Assert.NotEmpty(answer.Messages));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InterleavedGenerationsMeterEveryCallExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        const int calls = 4;
        var gate = new Gate(calls);
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithChatModel(gate.Client).StartAsync(ct);
        var llm = brain.Get<ILLM>(Model);

        await Task.WhenAll(Enumerable.Range(0, calls).Select(i => llm.Generate(Ask("call " + i), ct)));

        // A call's signals are journalled before it returns, so the journal read after the calls holds all of them.
        var starts = (await brain.History<InferenceStarted>(llm, ct)).Select(s => s.OperationId).ToArray();
        var ends = (await brain.History<InferenceCompleted>(llm, ct)).Select(s => s.OperationId).ToArray();
        Assert.Equal(calls, gate.Overlap);
        Assert.Equal(calls, gate.Served);
        Assert.Equal(calls, starts.Length);
        Assert.Equal(calls, ends.Length);
        Assert.Equal(calls, starts.Distinct().Count());
        Assert.Equal(starts.Order(), ends.Order());
    }

    // A synapse made while a generation is running (06-05: synapses, no Watch) leaves every subscriber subscribed.
    [Trait("host", "own")]
    [Fact]
    public async Task ASubscriptionArrivingMidGenerationLeavesEverySubscriberSubscribed()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var model = new StubChatClient(async (_, _, token) =>
        {
            if (Interlocked.Increment(ref held) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        });
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithChatModel(model).StartAsync(ct);
        var llm = brain.Get<ILLM>(Model);
        await using var early = await brain.Observe<InferenceCompleted>(llm, ct);

        var first = llm.Generate(Ask("held"), ct);
        await entered.Task.WaitAsync(ct);
        var late = brain.Observe<InferenceCompleted>(llm, ct);
        release.SetResult();
        await first;
        await using var subscribed = await late;
        await llm.Generate(Ask("after"), ct);

        var held1 = await early.NextAsync(ct: ct);
        var after = await early.NextAsync(ct: ct);
        Assert.NotEqual(held1.OperationId, after.OperationId);
        Assert.Equal(after.OperationId, (await subscribed.NextAsync(signal => signal.OperationId == after.OperationId, ct)).OperationId);
    }

    private static InferenceRequest Ask(string text) => new([new("user", [new AiText(text)])]);

    // Holds every call until `width` calls are inside the model at once: overlap, not elapsed time.
    private sealed class Gate(int width)
    {
        private readonly TaskCompletionSource _full = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inside;
        private int _peak;
        private int _served;

        public int Overlap => Volatile.Read(ref _peak);
        public int Served => Volatile.Read(ref _served);

        public StubChatClient Client => new(async (_, _, ct) =>
        {
            var inside = Interlocked.Increment(ref _inside);
            Raise(ref _peak, inside);
            if (inside >= width) { _full.TrySetResult(); }
            try { await _full.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
            finally { Interlocked.Decrement(ref _inside); }
            Interlocked.Increment(ref _served);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        });

        private static void Raise(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }
}
