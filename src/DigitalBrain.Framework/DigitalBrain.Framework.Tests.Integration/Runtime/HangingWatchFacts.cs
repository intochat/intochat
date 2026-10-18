using System.Collections.Concurrent;

namespace DigitalBrain.Framework.Tests.Integration.Runtime;

// The Orleans half of HangingWatchFacts (the in-memory half is Runtime.InMemory.Tests.Unit/HangingWatchFacts), with
// the same contracts and assertions: a watch that never answers holds only the watcher's lane, the watcher's caller
// times out at the neutral [CallTimeout], and the watcher answers again once the source does.
[Trait("host", "own")]
public sealed class HangingWatchFacts(NeutralMarkerFacts.MarkerHost host) : IClassFixture<NeutralMarkerFacts.MarkerHost>
{
    [Fact]
    public async Task ASourceWhoseWatchHangsHoldsOnlyTheWatcher()
    {
        var ct = TestContext.Current.CancellationToken;
        var within = host.Brain.Execution.AssertionTimeout;
        var run = Guid.NewGuid().ToString("N");
        var source = $"hanging/source-{run}";
        var watcher = host.Brain.Get<IHangingWatcher>($"hanging/watcher-{run}");
        var pinger = host.Brain.Get<IHangingPinger>($"hanging/pinger-{run}");
        var hanging = host.Brain.Get<IHangingWatchSource>(source);

        var watch = watcher.WatchSource(source);
        await HangingWatchSource.Entered(source).WaitAsync(within, ct);

        Assert.Equal(1, await pinger.Ping().WaitAsync(within, ct));
        Assert.Empty((await hanging.Read(0, 10).WaitAsync(within, ct)).Signals);
        var queued = watcher.Pings();
        await Assert.ThrowsAsync<TimeoutException>(() => watch.WaitAsync(within, ct));
        Assert.False(queued.IsCompleted);

        HangingWatchSource.Release(source);
        Assert.Equal(0, await queued.WaitAsync(within, ct));
    }
}

[Alias("tests.framework.hanging-watch-source")]
public interface IHangingWatchSource : INeuron;

public sealed class HangingWatchSource : Neuron, IHangingWatchSource
{
    private static readonly ConcurrentDictionary<string, (TaskCompletionSource Entered, TaskCompletionSource Release)> Gates = new(StringComparer.Ordinal);

    private static (TaskCompletionSource Entered, TaskCompletionSource Release) Gate(string key) =>
        Gates.GetOrAdd(key, _ => (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)));

    public static Task Entered(string key) => Gate(key).Entered.Task;

    public static void Release(string key) => Gate(key).Release.TrySetResult();

    public override async Task<Guid> Watch(INeuronObserver observer)
    {
        var gate = Gate(Context.Key);
        gate.Entered.TrySetResult();
        await gate.Release.Task;
        return await base.Watch(observer);
    }
}

[Alias("tests.framework.hanging-watcher")]
public interface IHangingWatcher : INeuron
{
    [CallTimeout("00:00:01")] Task WatchSource(string key);
    Task<int> Pings();
}

public sealed class HangingWatcher : Neuron, IHangingWatcher, INeuronObserver
{
    public Task WatchSource(string key) => Context.Resolver.ResolveKey<IHangingWatchSource>(key).Watch(Context.Self<INeuronObserver>());

    public Task<int> Pings() => Task.FromResult(0);

    public Task Nudge(SignalNudge nudge) => Task.CompletedTask;
}

[Alias("tests.framework.hanging-pinger")]
public interface IHangingPinger : INeuron
{
    Task<int> Ping();
}

public sealed class HangingPinger : Neuron, IHangingPinger
{
    private int _pings;

    public Task<int> Ping() => Task.FromResult(++_pings);
}
