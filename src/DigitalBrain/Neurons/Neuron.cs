using DigitalBrain.Spi;

namespace DigitalBrain;

/// <summary>
/// A neuron: a plain class, addressed by <see cref="NeuronId"/>, that reacts to signals one turn at a time.
/// Reactions are <see cref="OnAttribute"/> methods; they receive signals deduped and ordered per source.
/// </summary>
public abstract class Neuron
{
    private INeuronContext? _context;

    /// <summary>This neuron's id.</summary>
    protected NeuronId Id => Context.Id;

    /// <summary>What the runtime gives this neuron. Available once attached.</summary>
    protected INeuronContext Context => _context ?? throw new InvalidOperationException("The neuron is not attached to a runtime.");

    internal void Attach(INeuronContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_context is not null) { throw new InvalidOperationException("The neuron is already attached."); }
        _context = context;
    }

    // Loads persisted state before the first turn; Neuron<TState> overrides it.
    internal virtual Task Load() => Task.CompletedTask;

    internal Task Activate(CancellationToken cancellationToken) => OnActivate(cancellationToken);

    internal Task Deactivate(DeactivationCause cause, CancellationToken cancellationToken) => OnDeactivate(cause, cancellationToken);

    /// <summary>Publishes a signal without a state change. A state change publishes through <c>Save(next, signals)</c>.</summary>
    protected Task Publish(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Context.Publish(signal);
    }

    /// <summary>A typed interface to another neuron of the same brain.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> belongs to another brain.</exception>
    protected T Get<T>(NeuronId id) where T : class
    {
        if (id.Brain != Id.Brain) { throw new ArgumentException($"Neuron {id} belongs to another brain than {Id}.", nameof(id)); }
        return Context.Get<T>(id);
    }

    /// <summary>Runs <paramref name="work"/> after the current turn ends.</summary>
    protected void Defer(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Context.Defer(work);
    }

    /// <summary>Runs when the activation starts, after state is loaded.</summary>
    protected virtual Task OnActivate(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs when the activation ends.</summary>
    protected virtual Task OnDeactivate(DeactivationCause cause, CancellationToken cancellationToken) => Task.CompletedTask;
}
