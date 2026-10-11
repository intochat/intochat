using DigitalBrain.AI;
using DigitalBrain.AI.Inference;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Models.Signals;
using Microsoft.Extensions.AI;

namespace DigitalBrain.Modules.AI.Tests;

public sealed class InterleavingFacts
{
    private static readonly string Model = ModelAddress.Key("owner");

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

    [Fact]
    public async Task InterleavedGenerationsMeterEveryCallExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        const int calls = 4;
        var gate = new Gate(calls);
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithChatModel(gate.Client).StartAsync(ct);
        var llm = brain.Get<ILLM>(Model);
        await using var started = await brain.Observe<InferenceStarted>(llm, ct);
        await using var completed = await brain.Observe<InferenceCompleted>(llm, ct);

        await Task.WhenAll(Enumerable.Range(0, calls).Select(i => llm.Generate(Ask("call " + i), ct)));

        var starts = new List<string>();
        var ends = new List<string>();
        for (var i = 0; i < calls; i++)
        {
            starts.Add((await started.NextAsync(ct: ct)).OperationId);
            ends.Add((await completed.NextAsync(ct: ct)).OperationId);
        }
        Assert.Equal(calls, gate.Overlap);
        Assert.Equal(calls, gate.Served);
        Assert.Equal(calls, starts.Distinct().Count());
        Assert.Equal(starts.Order(), ends.Order());
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
