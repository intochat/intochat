namespace DigitalBrain.Spi;

// The runtime contract: interfaces only. A runtime implements these; neurons consume them.

/// <summary>What a runtime gives a neuron, attached once before activation.</summary>
public interface INeuronContext
{
    /// <summary>The neuron's id.</summary>
    NeuronId Id { get; }

    /// <summary>The services of the hosting brain.</summary>
    IServiceProvider Services { get; }

    /// <summary>The clock.</summary>
    TimeProvider Time { get; }

    /// <summary>The neuron's persisted state.</summary>
    INeuronState State { get; }

    /// <summary>Timers that live with the activation.</summary>
    ITimers Timers { get; }

    /// <summary>Durable reminders that reactivate the neuron.</summary>
    IReminders Reminders { get; }

    /// <summary>Long-running operations.</summary>
    IOperations Operations { get; }

    /// <summary>The synapses this neuron holds as a subscriber.</summary>
    ISynapses Synapses { get; }

    /// <summary>The brain's signal history.</summary>
    ISignalHistory History { get; }

    /// <summary>Publishes a signal from this neuron.</summary>
    Task Publish(Signal signal);

    /// <summary>A typed interface to another neuron of the same brain.</summary>
    T Get<T>(NeuronId id) where T : class;

    /// <summary>Runs <paramref name="work"/> after the current turn ends.</summary>
    void Defer(Func<Task> work);
}

/// <summary>A neuron's persisted state.</summary>
public interface INeuronState
{
    /// <summary>The stored state, or default when none exists.</summary>
    Task<T?> Load<T>();

    /// <summary>
    /// Stores <paramref name="next"/> and journals <paramref name="signals"/>, in order, as one journal entry:
    /// either both are durable or neither is. Signals reach subscribers only after the entry is durable.
    /// </summary>
    Task Save<T>(T next, IReadOnlyList<Signal> signals);

    /// <summary>Deletes the stored state.</summary>
    Task Clear();
}

/// <summary>Timers that live with the activation. Ticks run as turns.</summary>
public interface ITimers
{
    /// <summary>Starts or replaces the named timer. A null period fires once.</summary>
    void Start(string name, TimeSpan due, TimeSpan? period);

    /// <summary>Stops the named timer.</summary>
    void Stop(string name);
}

/// <summary>Durable reminders that reactivate the neuron. Ticks run as turns.</summary>
public interface IReminders
{
    /// <summary>Starts or replaces the named reminder.</summary>
    Task Start(string name, TimeSpan due, TimeSpan period);

    /// <summary>Stops the named reminder.</summary>
    Task Stop(string name);

    /// <summary>The names of the registered reminders.</summary>
    Task<IReadOnlyList<string>> Names();
}

/// <summary>Long-running operations a neuron tracks by id.</summary>
public interface IOperations
{
    /// <summary>Begins an operation and returns its id.</summary>
    Task<string> Begin(string kind, CancellationToken cancellationToken);

    /// <summary>Completes an operation.</summary>
    Task Complete(string id, bool succeeded, CancellationToken cancellationToken);
}

/// <summary>Typed proxies to neurons.</summary>
public interface INeuronProxies
{
    /// <summary>A typed proxy to the neuron with this id.</summary>
    T Get<T>(NeuronId id) where T : class;
}
