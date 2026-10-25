using DigitalBrain.Spi;

namespace DigitalBrain;

/// <summary>A neuron: an addressable unit that reacts to signals one turn at a time.</summary>
public abstract class Neuron
{
    private INeuronContext? _context;

    /// <summary>This neuron's id.</summary>
    protected NeuronId Id => Context.Id;

    /// <summary>What the runtime gives this neuron. Available once attached.</summary>
    protected INeuronContext Context => _context ?? throw new InvalidOperationException("The neuron is not attached to a runtime.");

    /// <summary>Called by a runtime once, before activation.</summary>
    /// <exception cref="InvalidOperationException">The neuron is already attached.</exception>
    public void Attach(INeuronContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_context is not null) { throw new InvalidOperationException("The neuron is already attached."); }
        _context = context;
    }

    /// <summary>Publishes a signal from this neuron.</summary>
    protected Task Publish(Signal signal) => Context.Publish(signal);

    /// <summary>A typed proxy to another neuron.</summary>
    protected T Get<T>(NeuronId id) where T : class => Context.Get<T>(id);

    /// <summary>Runs when the activation starts.</summary>
    protected virtual Task OnActivate(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs when the activation ends.</summary>
    protected virtual Task OnDeactivate(DeactivationCause cause, CancellationToken cancellationToken) => Task.CompletedTask;
}
