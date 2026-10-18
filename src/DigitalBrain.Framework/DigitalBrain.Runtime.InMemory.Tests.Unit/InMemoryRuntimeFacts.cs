using Microsoft.Extensions.Time.Testing;

namespace DigitalBrain.Runtime.InMemory.Tests.Unit;

// The in-memory runtime's guarantees (clean-core 2.5), on a FakeTimeProvider: no host, no clock waits.
public sealed class InMemoryRuntimeFacts
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReferenceActivatesTheNeuronAtItsAddress()
    {
        await using var runtime = Runtimes.Create(new FakeTimeProvider());
        var worker = runtime.Resolve<IWorker>(new BrainId("brain-1"), "work/a");

        var seen = await worker.Describe();

        Assert.Equal(["workerneuron/brain-1/work/a", "brain-1/work/a", "workerneuron/brain-1/work/a", "brain-1"], seen);
        Assert.Equal(worker, runtime.ResolveKey<IWorker>("brain-1/work/a"));
    }

    [Fact]
    public async Task AnUnmarkedCallWaitsForTheTurnAndAnInterleavesCallDoesNot()
    {
        var time = new FakeTimeProvider();
        await using var runtime = Runtimes.Create(time);
        var worker = runtime.ResolveKey<IWorker>("brain-1/turns");
        var hold = worker.Hold();
        await Gate.Of("brain-1/turns").Entered.Task.WaitAsync(_ct);

        Assert.Equal(0, await worker.Peek().WaitAsync(_ct));
        var queued = worker.Count();
        await Task.Yield();
        Assert.False(queued.IsCompleted);

        Gate.Of("brain-1/turns").Release.TrySetResult();
        await hold.WaitAsync(_ct);
        Assert.Equal(0, await queued.WaitAsync(_ct));
    }

    [Fact]
    public async Task AOneWayCallReturnsAtOnceAndRunsBehindTheTurn()
    {
        await using var runtime = Runtimes.Create(new FakeTimeProvider());
        var worker = runtime.ResolveKey<IWorker>("brain-1/one-way");
        var hold = worker.Hold();
        await Gate.Of("brain-1/one-way").Entered.Task.WaitAsync(_ct);

        await worker.Post(5).WaitAsync(_ct);
        Assert.Equal(0, await worker.Peek().WaitAsync(_ct));

        Gate.Of("brain-1/one-way").Release.TrySetResult();
        await hold.WaitAsync(_ct);
        await runtime.Idle().WaitAsync(_ct);
        Assert.Equal(5, await worker.Count().WaitAsync(_ct));
    }

    [Fact]
    public async Task ACallTimesOutAtItsCallTimeoutOnTheRuntimeClock()
    {
        var time = new FakeTimeProvider();
        await using var runtime = Runtimes.Create(time);
        var worker = runtime.ResolveKey<IWorker>("brain-1/stall");
        var stall = worker.Stall();
        await Task.Yield();

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(stall.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => stall.WaitAsync(_ct));
        Gate.Of("brain-1/stall").Release.TrySetResult();
    }

    [Fact]
    public async Task ACallCycleFailsAtTheDefaultTimeoutInsteadOfHanging()
    {
        var time = new FakeTimeProvider();
        await using var runtime = Runtimes.Create(time);
        var a = runtime.ResolveKey<IWorker>("brain-1/cycle-a");

        var cycle = a.CallBack("brain-1/cycle-b");
        await WhileRunning(runtime, () => time.Advance(InMemoryRuntime.DefaultCallTimeout - TimeSpan.FromSeconds(1)));
        Assert.False(cycle.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => cycle.WaitAsync(_ct));
    }

    [Fact]
    public async Task ASelfCallQueuesBehindTheCurrentTurn()
    {
        var time = new FakeTimeProvider();
        await using var runtime = Runtimes.Create(time);
        var worker = runtime.ResolveKey<IWorker>("brain-1/self");

        await worker.PostSelf(3).WaitAsync(_ct);
        await runtime.Idle().WaitAsync(_ct);
        Assert.Equal(3, await worker.Count().WaitAsync(_ct));

        var awaitedSelf = worker.CallSelf();
        await WhileRunning(runtime, () => time.Advance(InMemoryRuntime.DefaultCallTimeout));
        await Assert.ThrowsAsync<TimeoutException>(() => awaitedSelf.WaitAsync(_ct));
    }

    [Fact]
    public async Task DeactivateEndsTheActivationAfterTheTurn()
    {
        await using var runtime = Runtimes.Create(new FakeTimeProvider());
        var worker = runtime.ResolveKey<IWorker>("brain-1/deactivate");
        var first = await worker.Activation();

        await worker.Deactivate();

        Assert.NotEqual(first, await worker.Activation());
    }

    [Fact]
    public async Task StateSurvivesDeactivationThroughTheCodec()
    {
        await using var runtime = Runtimes.Create(new FakeTimeProvider());
        var counter = runtime.ResolveKey<ICounter>("brain-1/counter");
        Assert.False(await counter.Exists());

        Assert.Equal(2, await counter.Add(2));
        Assert.Equal(5, await counter.Add(3));
        await counter.Deactivate();

        Assert.True(await counter.Exists());
        Assert.Equal(5, await counter.Current());
    }

    [Fact]
    public async Task ASaveJournalsItsSignalAndNudgesWatchers()
    {
        await using var runtime = Runtimes.Create(new FakeTimeProvider());
        var counter = runtime.ResolveKey<ICounter>("brain-1/journal");
        var observer = new RecordingObserver();
        await counter.Watch(observer);

        await counter.Add(4);
        await runtime.Idle().WaitAsync(_ct);

        var page = await counter.Read(0, 10);
        var signal = Assert.IsType<Counted>(Assert.Single(page.Signals));
        Assert.Equal(4, signal.Count);
        Assert.Equal("counterneuron/brain-1/journal", signal.Publisher.Name);
        Assert.Equal(page.Sequences.Single(), Assert.Single(observer.Nudges).Sequence);
    }

    [Fact]
    public async Task TimersTickOnAdvanceAndStop()
    {
        var time = new FakeTimeProvider();
        await using var runtime = Runtimes.Create(time);
        var ticker = runtime.ResolveKey<ITicker>("brain-1/ticker");

        await ticker.Start(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        for (var i = 0; i < 3; i++) { time.Advance(TimeSpan.FromSeconds(1)); await runtime.Idle(); }
        Assert.Equal(3, await ticker.Ticks());

        await ticker.Stop();
        time.Advance(TimeSpan.FromSeconds(5));
        await runtime.Idle();
        Assert.Equal(3, await ticker.Ticks());
    }

    // Lets queued turns reach their timeouts before the clock moves.
    internal static async Task WhileRunning(InMemoryRuntime runtime, Action advance)
    {
        for (var i = 0; i < 10; i++) { await Task.Yield(); }
        advance();
        _ = runtime;
    }
}
