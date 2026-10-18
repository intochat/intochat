using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.InMemory;

// One address: its activation, its turn lock and its timers. A call takes the turn unless its method interleaves;
// a one-way call is queued behind the turn and not awaited; every awaited call has a response timeout on the
// runtime's clock, so an A -> B -> A cycle fails as it does in Orleans instead of hanging.
internal sealed class NeuronHost : INeuronContext, IAsyncDisposable
{
    private readonly InMemoryRuntime _runtime;
    private readonly Type _class;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly SemaphoreSlim _activating = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ITimer> _timers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITimer> _reminders = new(StringComparer.Ordinal);
    private Neuron? _neuron;
    private bool _deactivate;
    private int _outstanding;
    private TaskCompletionSource _quiet = Completed();

    public NeuronHost(InMemoryRuntime runtime, NeuronAddress address, Type neuronClass)
    {
        _runtime = runtime;
        _class = neuronClass;
        Address = address;
        Timers = new HostTimers(this);
        Fanout = new MemoryFanout(address);
    }

    public NeuronAddress Address { get; }

    public string Key => Address.Key;

    public IServiceProvider Services => _runtime.Services;

    public TimeProvider Time => _runtime.Time;

    public INeuronTimers Timers { get; }

    public ICallerContextCarrier Caller { get; } = new GrainNeuronContext.RequestCallerCarrier();

    public ITraceCarrier Trace { get; } = new GrainNeuronContext.TraceCarrier();

    public ISignalFanout Fanout { get; }

    public INeuronResolver Resolver => _runtime;

    public T Self<T>() where T : class => _runtime.Reference<T>(Address);

    // Nothing is collected while idle in memory, so there is nothing to delay.
    public void KeepAlive(TimeSpan? duration) { }

    public void Deactivate() => _deactivate = true;

    public Task<object?> Call(MethodInfo method, object?[] arguments)
    {
        var markers = CallMarkers.Of(_class, method, arguments);
        var work = Track(Turn(neuron => Invoke(neuron, method, arguments), markers.Interleaves));
        if (markers.OneWay) { return Task.FromResult<object?>(null); }
        return work.WaitAsync(markers.Timeout ?? InMemoryRuntime.DefaultCallTimeout, _runtime.Time);
    }

    public Task Idle()
    {
        lock (_gate) { return _quiet.Task; }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            foreach (var timer in _timers.Values.Concat(_reminders.Values)) { timer.Dispose(); }
            _timers.Clear();
            _reminders.Clear();
        }
        await Task.CompletedTask;
    }

    private Task Tick(Func<CancellationToken, Task> tick, bool interleave) =>
        Track(Turn(async _ => { await tick(CancellationToken.None); return null; }, interleave));

    private Task<object?> Track(Task<object?> work)
    {
        lock (_gate)
        {
            if (_outstanding++ == 0) { _quiet = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        }
        _ = work.ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (--_outstanding == 0) { _quiet.TrySetResult(); }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return work;
    }

    private async Task<object?> Turn(Func<Neuron, Task<object?>> body, bool interleave)
    {
        // Leave the caller's synchronous stack first, so a queued call never runs inside the caller's turn.
        await Task.Yield();
        if (!interleave) { await _turn.WaitAsync(); }
        try { return await body(await Activated()); }
        finally
        {
            if (!interleave)
            {
                if (_deactivate) { await DeactivateNow(); }
                _turn.Release();
            }
        }
    }

    private async Task<Neuron> Activated()
    {
        if (_neuron is { } active) { return active; }
        await _activating.WaitAsync();
        try
        {
            if (_neuron is { } raced) { return raced; }
            var neuron = (Neuron)ActivatorUtilities.CreateInstance(_runtime.Services, _class, [.. await States()]);
            neuron.Attach(this);
            await neuron.OnActivateAsync(CancellationToken.None);
            _neuron = neuron;
            return neuron;
        }
        finally { _activating.Release(); }
    }

    private async Task DeactivateNow()
    {
        _deactivate = false;
        if (_neuron is not { } neuron) { return; }
        _neuron = null;
        lock (_gate)
        {
            foreach (var timer in _timers.Values) { timer.Dispose(); }
            _timers.Clear();
        }
        await neuron.OnDeactivateAsync(new DeactivationReason(DeactivationReasonCode.ApplicationRequested, "Deactivate was requested."), CancellationToken.None);
        // On Orleans the grain lifecycle runs the neutral hook; here the host does.
        if (neuron is IDeactivatingNeuron deactivating) { await deactivating.OnDeactivating(DeactivationCause.Requested, CancellationToken.None); }
    }

    // One state object per [PersistentState] constructor parameter, read from the runtime's store.
    private async Task<object[]> States()
    {
        var states = new List<object>();
        var constructor = _class.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        foreach (var parameter in constructor?.GetParameters() ?? [])
        {
            if (parameter.GetCustomAttribute<PersistentStateAttribute>() is not { } attribute) { continue; }
            var stateType = parameter.ParameterType.GetGenericArguments()[0];
            var state = (IMemoryState)Activator.CreateInstance(typeof(MemoryPersistentState<>).MakeGenericType(stateType), _runtime.State, _runtime.Codec, Address, attribute.StateName)!;
            await state.Read();
            states.Add(state);
        }
        return [.. states];
    }

    private static async Task<object?> Invoke(Neuron neuron, MethodInfo method, object?[] arguments)
    {
        object? result;
        try { result = method.Invoke(neuron, arguments); }
        catch (TargetInvocationException invocation) when (invocation.InnerException is { } inner)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
        switch (result)
        {
            case Task task:
                await task;
                return method.ReturnType.IsGenericType ? method.ReturnType.GetProperty(nameof(Task<object>.Result))!.GetValue(task) : null;
            case ValueTask valueTask:
                await valueTask;
                return null;
            default:
                if (result is not null && method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    var asTask = (Task)method.ReturnType.GetMethod(nameof(ValueTask<object>.AsTask))!.Invoke(result, null)!;
                    await asTask;
                    return asTask.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(asTask);
                }
                return result;
        }
    }

    private static TaskCompletionSource Completed()
    {
        var completed = new TaskCompletionSource();
        completed.SetResult();
        return completed;
    }

    // Timers fire on the runtime's TimeProvider and run as turns; reminders outlive deactivation and reactivate.
    private sealed class HostTimers(NeuronHost host) : INeuronTimers
    {
        public IDisposable Start(string name, TimeSpan due, TimeSpan? period, Func<CancellationToken, Task> tick, bool interleave = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(tick);
            ITimer? timer = null;
            timer = host._runtime.Time.CreateTimer(_ =>
            {
                lock (host._gate)
                {
                    if (!host._timers.TryGetValue(name, out var current) || !ReferenceEquals(current, timer)) { return; }
                    if (period is null) { host._timers.Remove(name); }
                }
                _ = host.Tick(tick, interleave);
            }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            lock (host._gate)
            {
                if (host._timers.Remove(name, out var previous)) { previous.Dispose(); }
                host._timers[name] = timer;
            }
            timer.Change(due, period ?? Timeout.InfiniteTimeSpan);
            return new Stop(host, host._timers, name, timer);
        }

        public Task Remind(string name, TimeSpan due, TimeSpan period)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (period <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(period)); }
            var timer = host._runtime.Time.CreateTimer(_ => _ = host.Tick(async ct =>
            {
                if (host._neuron is IRemindedNeuron reminded) { await reminded.OnReminder(name, ct); }
            }, interleave: false), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            lock (host._gate)
            {
                if (host._reminders.Remove(name, out var previous)) { previous.Dispose(); }
                host._reminders[name] = timer;
            }
            timer.Change(due, period);
            return Task.CompletedTask;
        }

        public Task Forget(string name)
        {
            lock (host._gate)
            {
                if (host._reminders.Remove(name, out var timer)) { timer.Dispose(); }
            }
            return Task.CompletedTask;
        }
    }

    private sealed class Stop(NeuronHost host, Dictionary<string, ITimer> timers, string name, ITimer timer) : IDisposable
    {
        public void Dispose()
        {
            lock (host._gate)
            {
                if (timers.TryGetValue(name, out var current) && ReferenceEquals(current, timer)) { timers.Remove(name); }
            }
            timer.Dispose();
        }
    }

    // The runtime's own fanout: a dictionary of observers per source, best effort like the Orleans one.
    private sealed class MemoryFanout(NeuronAddress self) : ISignalFanout
    {
        private readonly ConcurrentDictionary<INeuronObserver, IReadOnlySet<string>?> _watchers = new();

        public void Watch(NeuronAddress source, INeuronObserver observer, IReadOnlySet<string>? signals)
        {
            Own(source);
            _watchers[observer] = signals;
        }

        public void Unwatch(NeuronAddress source, INeuronObserver observer)
        {
            Own(source);
            _watchers.TryRemove(observer, out _);
        }

        public Task Nudge(NeuronAddress source, SignalNudge nudge, IReadOnlyList<Signal> appended)
        {
            Own(source);
            foreach (var (observer, signals) in _watchers)
            {
                if (signals is null || appended.Any(signal => signals.Contains(signal.GetType().Name))) { _ = observer.Nudge(nudge); }
            }
            return Task.CompletedTask;
        }

        private void Own(NeuronAddress source)
        {
            if (source != self) { throw new ArgumentException($"An activation fans out only its own signals, not {source}.", nameof(source)); }
        }
    }
}
