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
        var watcher = new Recorder(llm);
        await watcher.Start(brain);

        await Task.WhenAll(Enumerable.Range(0, calls).Select(i => llm.Generate(Ask("call " + i), ct)));

        // A call's signals are journalled before it returns, so reading after the cursor sees all of them.
        await watcher.Pull();
        var starts = watcher.Of<InferenceStarted>().Select(s => s.OperationId).ToArray();
        var ends = watcher.Of<InferenceCompleted>().Select(s => s.OperationId).ToArray();
        Assert.Equal(calls, gate.Overlap);
        Assert.Equal(calls, gate.Served);
        Assert.Equal(calls, starts.Length);
        Assert.Equal(calls, ends.Length);
        Assert.Equal(calls, starts.Distinct().Count());
        Assert.Equal(starts.Order(), ends.Order());
    }

    [Trait("host", "own")]
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
        var early = new Recorder(llm);
        var late = new Recorder(llm);
        await early.Start(brain);

        var first = llm.Generate(Ask("held"), ct);
        await entered.Task.WaitAsync(ct);
        var watch = late.Start(brain);
        release.SetResult();
        await first;
        await watch;
        await llm.Generate(Ask("after"), ct);
        await early.Pull();
        await late.Pull();

        Assert.Equal(2, early.Of<InferenceStarted>().Length);
        Assert.Equal(2, early.Of<InferenceCompleted>().Length);
        var after = early.Of<InferenceCompleted>()[^1].OperationId;
        Assert.Contains(late.Of<InferenceStarted>(), signal => signal.OperationId == after);
        Assert.Equal(after, late.Of<InferenceCompleted>()[^1].OperationId);
    }

    private static IGrainFactory Factory(ITestBrain brain) => brain.Services.GetRequiredService<IGrainFactory>();

    // On a nudge, reads the source's journal after its own cursor, page by page (as the Framework test watchers do).
    private sealed class Recorder(INeuron source) : INeuronObserver
    {
        private const int PageSize = 100;
        private readonly SemaphoreSlim _reading = new(1, 1);
        private readonly List<Signal> _signals = [];
        private long _cursor;

        public T[] Of<T>() where T : Signal { lock (_signals) { return _signals.OfType<T>().ToArray(); } }

        // The cursor starts before the watch, so nothing published in between is missed.
        public async Task Start(ITestBrain brain)
        {
            _cursor = (await source.Read(0, 0)).Last;
            await source.Watch(Factory(brain).CreateObjectReference<INeuronObserver>(this));
        }

        public Task Nudge(SignalNudge nudge) => Pull();

        public async Task Pull()
        {
            await _reading.WaitAsync();
            try
            {
                while (true)
                {
                    var page = await source.Read(_cursor, PageSize);
                    lock (_signals) { _signals.AddRange(page.Signals); }
                    if (page.Signals.Length < PageSize) { _cursor = Math.Max(_cursor, page.Last); return; }
                    _cursor = page.Sequences[^1];
                }
            }
            finally { _reading.Release(); }
        }
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
