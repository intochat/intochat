using DigitalBrain.Capabilities;
using DigitalBrain.Spi;

namespace DigitalBrain.Testing;

/// <summary>A clock that moves only when told.</summary>
public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now;

    /// <summary>Starts at <paramref name="start"/>, or 2026-01-01 UTC.</summary>
    public ManualTime(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        _now += by;
    }
}

/// <summary>Neuron state held in memory.</summary>
public sealed class InMemoryNeuronState : INeuronState
{
    private object? _value;

    /// <summary>True when a value is stored.</summary>
    public bool Exists => _value is not null;

    /// <summary>How many times <see cref="Save{T}(T)"/> ran.</summary>
    public int Saves { get; private set; }

    /// <inheritdoc />
    public Task<T?> Load<T>() => Task.FromResult(_value is T value ? value : default);

    /// <inheritdoc />
    public Task Save<T>(T state)
    {
        _value = state;
        Saves++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Clear()
    {
        _value = null;
        return Task.CompletedTask;
    }
}

/// <summary>Timers that fire only when <see cref="Advance(TimeSpan)"/> moves their clock.</summary>
public sealed class ManualTimers : ITimers
{
    private readonly ManualTime _time;
    private readonly Dictionary<string, (DateTimeOffset Due, TimeSpan? Period)> _timers = new(StringComparer.Ordinal);

    /// <summary>Timers on the given clock.</summary>
    public ManualTimers(ManualTime time) => _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <summary>Runs for each fired timer name, in due order.</summary>
    public Func<string, Task>? OnFire { get; set; }

    /// <summary>The names of the running timers.</summary>
    public IReadOnlyCollection<string> Names => _timers.Keys;

    /// <inheritdoc />
    public void Start(string name, TimeSpan due, TimeSpan? period)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _timers[name] = (_time.GetUtcNow() + due, period);
    }

    /// <inheritdoc />
    public void Stop(string name) => _timers.Remove(name);

    /// <summary>Moves the clock and fires every due timer; returns the fired names in order.</summary>
    public async Task<IReadOnlyList<string>> Advance(TimeSpan by)
    {
        var end = _time.GetUtcNow() + by;
        var fired = new List<string>();
        while (true)
        {
            var next = _timers.Where(timer => timer.Value.Due <= end).OrderBy(timer => timer.Value.Due).ThenBy(timer => timer.Key, StringComparer.Ordinal).FirstOrDefault();
            if (next.Key is null) { break; }
            _time.Advance(next.Value.Due - _time.GetUtcNow() is var step && step > TimeSpan.Zero ? step : TimeSpan.Zero);
            if (next.Value.Period is { } period && period > TimeSpan.Zero) { _timers[next.Key] = (next.Value.Due + period, period); }
            else { _timers.Remove(next.Key); }
            fired.Add(next.Key);
            if (OnFire is not null) { await OnFire(next.Key).ConfigureAwait(false); }
        }
        _time.Advance(end - _time.GetUtcNow());
        return fired;
    }
}

/// <summary>Reminders that fire only through <see cref="Fire(string)"/>.</summary>
public sealed class ManualReminders : IReminders
{
    private readonly Dictionary<string, (TimeSpan Due, TimeSpan Period)> _reminders = new(StringComparer.Ordinal);

    /// <summary>The neuron that receives <see cref="IRemindedNeuron.OnReminder"/>.</summary>
    public object? Target { get; set; }

    /// <inheritdoc />
    public Task Start(string name, TimeSpan due, TimeSpan period)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _reminders[name] = (due, period);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Stop(string name)
    {
        _reminders.Remove(name);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> Names() => Task.FromResult<IReadOnlyList<string>>([.. _reminders.Keys.Order(StringComparer.Ordinal)]);

    /// <summary>Delivers the named reminder to the attached neuron.</summary>
    /// <exception cref="InvalidOperationException">The target does not implement <see cref="IRemindedNeuron"/>.</exception>
    public Task Fire(string name, CancellationToken cancellationToken = default)
        => Target is IRemindedNeuron reminded
            ? reminded.OnReminder(name, cancellationToken)
            : throw new InvalidOperationException($"{Target?.GetType().FullName ?? "No neuron"} does not implement {nameof(IRemindedNeuron)}.");
}

/// <summary>Synapses held in memory, with a record of every change.</summary>
public sealed class RecordingSynapses : ISynapses
{
    private readonly List<Synapse> _synapses = [];

    /// <summary>The current synapses.</summary>
    public IReadOnlyList<Synapse> Current => _synapses;

    /// <inheritdoc />
    public Task Connect(Synapse synapse)
    {
        ArgumentNullException.ThrowIfNull(synapse);
        if (!_synapses.Contains(synapse)) { _synapses.Add(synapse); }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task Disconnect(Synapse synapse)
    {
        _synapses.Remove(synapse);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Synapse>> Of(NeuronId from) => Task.FromResult<IReadOnlyList<Synapse>>([.. _synapses.Where(synapse => synapse.From == from)]);
}

/// <summary>Operations held in memory.</summary>
public sealed class FakeOperations : IOperations
{
    private readonly Dictionary<string, (string Kind, bool? Succeeded)> _operations = new(StringComparer.Ordinal);
    private int _next;

    /// <summary>Every operation: kind and outcome (null while running).</summary>
    public IReadOnlyDictionary<string, (string Kind, bool? Succeeded)> All => _operations;

    /// <inheritdoc />
    public Task<string> Begin(string kind, CancellationToken cancellationToken)
    {
        var id = $"op-{++_next}";
        _operations[id] = (kind, null);
        return Task.FromResult(id);
    }

    /// <inheritdoc />
    public Task Complete(string id, bool succeeded, CancellationToken cancellationToken)
    {
        if (!_operations.TryGetValue(id, out var operation)) { throw new KeyNotFoundException($"No operation {id}."); }
        _operations[id] = (operation.Kind, succeeded);
        return Task.CompletedTask;
    }
}

/// <summary>Neuron proxies registered by hand.</summary>
public sealed class FakeNeuronProxies : INeuronProxies
{
    private readonly Dictionary<(NeuronId, Type), object> _proxies = [];

    /// <summary>Makes <see cref="Get{T}(NeuronId)"/> return <paramref name="instance"/> for this id.</summary>
    public FakeNeuronProxies Register<T>(NeuronId id, T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        _proxies[(id, typeof(T))] = instance;
        return this;
    }

    /// <inheritdoc />
    /// <exception cref="KeyNotFoundException">No proxy is registered for the id.</exception>
    public T Get<T>(NeuronId id) where T : class
        => _proxies.TryGetValue((id, typeof(T)), out var proxy) ? (T)proxy
            : throw new KeyNotFoundException($"No {typeof(T).Name} is registered for neuron {id}.");
}

/// <summary>Capabilities registered by hand.</summary>
public sealed class FakeCapabilityBroker : ICapabilityBroker
{
    private readonly Dictionary<Type, object> _capabilities = [];

    /// <summary>Makes <see cref="Resolve{TCapability}"/> return <paramref name="instance"/> for every brain.</summary>
    public FakeCapabilityBroker Register<TCapability>(TCapability instance) where TCapability : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        _capabilities[typeof(TCapability)] = instance;
        return this;
    }

    /// <inheritdoc />
    public Task<TCapability> Resolve<TCapability>(BrainId brain, CancellationToken ct) where TCapability : class
        => _capabilities.TryGetValue(typeof(TCapability), out var capability)
            ? Task.FromResult((TCapability)capability)
            : throw new KeyNotFoundException($"No {typeof(TCapability).Name} capability is registered.");
}

/// <summary>A neuron context made of fakes. Published signals are recorded.</summary>
public sealed class FakeNeuronContext : INeuronContext
{
    private readonly List<Signal> _published = [];

    /// <summary>A context for the neuron with this id.</summary>
    public FakeNeuronContext(NeuronId id, IServiceProvider? services = null)
    {
        Id = id;
        Services = services ?? EmptyServices.Instance;
        Timers = new ManualTimers(Clock);
    }

    /// <inheritdoc />
    public NeuronId Id { get; }

    /// <inheritdoc />
    public IServiceProvider Services { get; }

    /// <summary>The manual clock behind <see cref="Time"/>.</summary>
    public ManualTime Clock { get; } = new();

    /// <inheritdoc />
    public TimeProvider Time => Clock;

    /// <summary>The in-memory state.</summary>
    public InMemoryNeuronState MemoryState { get; } = new();

    /// <inheritdoc />
    public INeuronState State => MemoryState;

    /// <summary>The manual timers.</summary>
    public ManualTimers Timers { get; }

    ITimers INeuronContext.Timers => Timers;

    /// <summary>The manual reminders.</summary>
    public ManualReminders Reminders { get; } = new();

    IReminders INeuronContext.Reminders => Reminders;

    /// <summary>The fake operations.</summary>
    public FakeOperations Operations { get; } = new();

    IOperations INeuronContext.Operations => Operations;

    /// <summary>The recording synapses.</summary>
    public RecordingSynapses Synapses { get; } = new();

    ISynapses INeuronContext.Synapses => Synapses;

    /// <summary>The proxies <see cref="Get{T}(NeuronId)"/> returns.</summary>
    public FakeNeuronProxies Proxies { get; } = new();

    /// <summary>Every signal published, in order.</summary>
    public IReadOnlyList<Signal> Published => _published;

    /// <inheritdoc />
    public Task Publish(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        _published.Add(signal);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public T Get<T>(NeuronId id) where T : class => Proxies.Get<T>(id);

    private sealed class EmptyServices : IServiceProvider
    {
        internal static readonly EmptyServices Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
