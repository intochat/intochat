using DigitalBrain.AI;
using DigitalBrain.AI.Inference;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Models.Signals;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

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
        var watcher = new Recorder();
        var reference = Factory(brain).CreateObjectReference<INeuronObserver>(watcher);
        await llm.Watch(reference);

        await Task.WhenAll(Enumerable.Range(0, calls).Select(i => llm.Generate(Ask("call " + i), ct)));

        // A publish awaits every observer, so all of a call's signals are delivered before it returns.
        var starts = watcher.Of<InferenceStarted>().Select(s => s.OperationId).ToArray();
        var ends = watcher.Of<InferenceCompleted>().Select(s => s.OperationId).ToArray();
        Assert.Equal(calls, gate.Overlap);
        Assert.Equal(calls, gate.Served);
        Assert.Equal(calls, starts.Length);
        Assert.Equal(calls, ends.Length);
        Assert.Equal(calls, starts.Distinct().Count());
        Assert.Equal(starts.Order(), ends.Order());
    }

    [Fact]
    public async Task AWatchArrivingMidGenerationLeavesEveryObserverSubscribed()
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
        var early = new Recorder();
        var late = new Recorder();
        await llm.Watch(Factory(brain).CreateObjectReference<INeuronObserver>(early));

        var first = llm.Generate(Ask("held"), ct);
        await entered.Task.WaitAsync(ct);
        var watch = llm.Watch(Factory(brain).CreateObjectReference<INeuronObserver>(late));
        release.SetResult();
        await first;
        await watch;
        await llm.Generate(Ask("after"), ct);

        Assert.Equal(2, early.Of<InferenceStarted>().Length);
        Assert.Equal(2, early.Of<InferenceCompleted>().Length);
        var after = early.Of<InferenceCompleted>()[^1].OperationId;
        Assert.Contains(late.Of<InferenceStarted>(), signal => signal.OperationId == after);
        Assert.Equal(after, late.Of<InferenceCompleted>()[^1].OperationId);
    }

    private static IGrainFactory Factory(ITestBrain brain) => brain.Services.GetRequiredService<IGrainFactory>();

    private sealed class Recorder : INeuronObserver
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Signal> _signals = new();
        public T[] Of<T>() where T : Signal => _signals.OfType<T>().ToArray();
        public Task OnSignalAsync(Signal signal) { _signals.Enqueue(signal); return Task.CompletedTask; }
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
