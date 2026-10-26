namespace DigitalBrain.Spi;

/// <summary>Marks a non-neuron endpoint contract a runtime can resolve like a neuron.</summary>
#pragma warning disable CA1040 // marker interface, kept from the os vocabulary
public interface IRuntimeEndpoint;
#pragma warning restore CA1040

/// <summary>Implemented by a neuron that releases resources when its activation ends.</summary>
public interface IDeactivatingNeuron
{
    /// <summary>Runs when the activation ends.</summary>
    Task OnDeactivating(DeactivationCause cause, CancellationToken cancellationToken);
}

/// <summary>Timers live with the activation; reminders are durable and reactivate the neuron. Ticks run as turns.</summary>
public interface INeuronTimers
{
    /// <summary>Replaces any timer of the same name. A null period fires once. keepAlive holds the activation while the timer runs.</summary>
    IDisposable Start(string name, TimeSpan due, TimeSpan? period, Func<CancellationToken, Task> tick, bool interleave = false, bool keepAlive = false);

    /// <summary>Registers or replaces a durable reminder.</summary>
    Task Remind(string name, TimeSpan due, TimeSpan period);

    /// <summary>Removes a durable reminder.</summary>
    Task Forget(string name);
}

/// <summary>Implemented by a neuron that registers reminders.</summary>
public interface IRemindedNeuron
{
    /// <summary>Runs when the named reminder fires.</summary>
    Task OnReminder(string name, CancellationToken cancellationToken);
}

/// <summary>The caller provenance the runtime carries with each call.</summary>
public interface ICallerContextCarrier
{
    /// <summary>Runs the scope with no caller stamped, restoring the caller afterwards.</summary>
    IDisposable Suppress();
}
