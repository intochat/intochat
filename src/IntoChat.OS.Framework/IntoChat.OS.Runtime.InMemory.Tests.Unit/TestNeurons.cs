using System.Collections.Concurrent;
using IntoChat.OS.Framework;
using IntoChat.OS.Runtime;
using Orleans.Runtime;
using DeactivationCause = DigitalBrain.DeactivationCause;
using IDeactivatingNeuron = DigitalBrain.Spi.IDeactivatingNeuron;

namespace IntoChat.OS.Runtime.InMemory.Tests.Unit;

// A gate a fact holds a turn with and releases.
public sealed class Gate
{
    private static readonly ConcurrentDictionary<string, Gate> Gates = new(StringComparer.Ordinal);

    public static Gate Of(string key) => Gates.GetOrAdd(key, _ => new());

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[GenerateSerializer, Alias("tests.inmemory.counted")]
public sealed record Counted([property: Id(0)] int Count) : Signal;

[GenerateSerializer, Alias("tests.inmemory.counter-state")]
public sealed class CounterState
{
    [Id(0)] public int Count { get; set; }
}

[Alias("tests.inmemory.worker")]
public interface IWorker : INeuron
{
    Task<string[]> Describe();
    Task Hold();
    Task<int> Count();
    [Interleaves] Task<int> Peek();
    [OneWayCall] Task Post(int count);
    [CallTimeout("00:00:02")] Task Stall();
    Task<int> CallBack(string otherKey);
    Task<int> CallSelf();
    Task PostSelf(int count);
    Task DeferPost(int count);
    Task DeferCount();
    Task Deactivate();
    [Interleaves] Task DeactivateInterleaved();
    Task<Guid> Activation();
}

public sealed class WorkerNeuron : Neuron, IWorker
{
    private readonly Guid _activation = Guid.NewGuid();
    private int _count;

    private Gate Gate => Gate.Of(Context.Key);

    public Task<string[]> Describe() =>
        Task.FromResult<string[]>([Context.Address.StorageKey, Context.Key, Id.Name, Context.Address.Brain?.Value ?? ""]);

    public async Task Hold()
    {
        Gate.Entered.TrySetResult();
        await Gate.Release.Task;
    }

    public Task<int> Count() => Task.FromResult(_count);

    public Task<int> Peek() => Task.FromResult(_count);

    public Task Post(int count)
    {
        _count = count;
        return Task.CompletedTask;
    }

    public Task Stall() => Gate.Release.Task;

    // A -> B -> A: B's call back into this neuron waits behind this turn until its timeout.
    public Task<int> CallBack(string otherKey) => Context.Resolver.ResolveKey<IWorker>(otherKey).CallBack(Context.Key);

    public Task<int> CallSelf() => Context.Self<IWorker>().Count();

    public Task PostSelf(int count) => Context.Self<IWorker>().Post(count);

    public Task DeferPost(int count) => Defer<IWorker>(self => self.Post(count));

    public Task DeferCount() => Defer<IWorker>(self => self.Count());

    public Task Deactivate()
    {
        Context.Deactivate();
        return Task.CompletedTask;
    }

    public Task DeactivateInterleaved()
    {
        Context.Deactivate();
        return Task.CompletedTask;
    }

    public Task<Guid> Activation() => Task.FromResult(_activation);
}

[Alias("tests.inmemory.counter")]
public interface ICounter : INeuron
{
    Task<int> Add(int amount);
    Task<int> Current();
    Task<bool> Exists();
    Task Deactivate();
}

public sealed class CounterNeuron([PersistentState("counter", "unused")] IPersistentState<CounterState> store) : Neuron<CounterState>(store), ICounter
{
    public async Task<int> Add(int amount)
    {
        var next = new CounterState { Count = Snapshot.Count + amount };
        await Save(next, new Counted(next.Count));
        return next.Count;
    }

    public Task<int> Current() => Task.FromResult(Snapshot.Count);

    Task<bool> ICounter.Exists() => Task.FromResult(Exists);

    public Task Deactivate()
    {
        Context.Deactivate();
        return Task.CompletedTask;
    }
}

[Alias("tests.inmemory.ticker")]
public interface ITicker : INeuron
{
    Task Start(TimeSpan due, TimeSpan? period);
    Task Stop();
    Task<int> Ticks();
}

public sealed class TickerNeuron : Neuron, ITicker
{
    private IDisposable? _timer;
    private int _ticks;

    public Task Start(TimeSpan due, TimeSpan? period)
    {
        _timer = Context.Timers.Start("tick", due, period, _ => { _ticks++; return Task.CompletedTask; }, keepAlive: true);
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    public Task<int> Ticks() => Task.FromResult(_ticks);
}

public sealed class RecordingObserver : INeuronObserver
{
    public ConcurrentQueue<SignalNudge> Nudges { get; } = new();

    public Task Nudge(SignalNudge nudge)
    {
        Nudges.Enqueue(nudge);
        return Task.CompletedTask;
    }
}

public static class Runtimes
{
    public static InMemoryRuntime Create(TimeProvider time) => InMemoryRuntime.Create(time, runtime => runtime
        .AddNeuron<WorkerNeuron>().AddNeuron<CounterNeuron>().AddNeuron<TickerNeuron>()
        .AddNeuron<FailingDeactivation>().AddNeuron<HangingSource>().AddNeuron<SourceWatcher>().AddNeuron<Pinger>());
}

[Alias("tests.inmemory.hanging-source")]
public interface IHangingSource : INeuron;

// A source whose watch, once armed, answers only when released.
public sealed class HangingSource : Neuron, IHangingSource
{
    public static void Arm(string key) => _ = Gate.Of("hang:" + key);

    public static Task Entered(string key) => Gate.Of("hang:" + key).Entered.Task;

    public static void Release(string key) => Gate.Of("hang:" + key).Release.TrySetResult();

    public override async Task<Guid> Watch(INeuronObserver observer)
    {
        var gate = Gate.Of("hang:" + Context.Key);
        gate.Entered.TrySetResult();
        await gate.Release.Task;
        return await base.Watch(observer);
    }
}

[Alias("tests.inmemory.source-watcher")]
public interface ISourceWatcher : INeuron
{
    [CallTimeout("00:00:01")] Task WatchSource(string key);
    Task<int> Pings();
}

public sealed class SourceWatcher : Neuron, ISourceWatcher, INeuronObserver
{
    public Task WatchSource(string key) => Context.Resolver.ResolveKey<IHangingSource>(key).Watch(Context.Self<INeuronObserver>());

    public Task<int> Pings() => Task.FromResult(0);

    public Task Nudge(SignalNudge nudge) => Task.CompletedTask;
}

[Alias("tests.inmemory.pinger")]
public interface IPinger : INeuron
{
    Task<int> Ping();
}

public sealed class Pinger : Neuron, IPinger
{
    private int _pings;

    public Task<int> Ping() => Task.FromResult(++_pings);
}

[Alias("tests.inmemory.failing-deactivation")]
public interface IFailingDeactivation : INeuron
{
    Task Deactivate();
    Task<Guid> Activation();
}

// Its deactivation hook throws; the address must stay callable.
public sealed class FailingDeactivation : Neuron, IFailingDeactivation, IDeactivatingNeuron
{
    private readonly Guid _activation = Guid.NewGuid();

    public Task Deactivate()
    {
        Context.Deactivate();
        return Task.CompletedTask;
    }

    public Task<Guid> Activation() => Task.FromResult(_activation);

    public Task OnDeactivating(DeactivationCause cause, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Deactivation failed.");
}

[Orleans.Concurrency.MayInterleave(nameof(Never))]
public sealed class OrleansPredicateNeuron : Neuron, IPinger
{
    public static bool Never(Orleans.Serialization.Invocation.IInvokable request) => request is null;

    public Task<int> Ping() => Task.FromResult(0);
}
