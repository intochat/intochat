using DigitalBrain.Modules;
using DigitalBrain.Kernel;

namespace DigitalBrain.Neurons;

/// <summary>The runtime's only entry points into a neuron's lifecycle. Module code does not call these.</summary>
public static class NeuronAttachment
{
    /// <summary>Attaches the runtime context, once.</summary>
    /// <exception cref="InvalidOperationException">The neuron is already attached.</exception>
    public static void Attach(Neuron neuron, INeuronContext context)
    {
        ArgumentNullException.ThrowIfNull(neuron);
        neuron.Attach(context);
    }

    /// <summary>Loads the neuron's state, then runs its activation.</summary>
    public static async Task Activate(Neuron neuron, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(neuron);
        await neuron.Load().ConfigureAwait(true);
        await neuron.Activate(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Runs the neuron's deactivation.</summary>
    public static Task Deactivate(Neuron neuron, DeactivationCause cause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(neuron);
        return neuron.Deactivate(cause, cancellationToken);
    }

    /// <summary>Delivers a timer or reminder tick. Module code does not call this.</summary>
    public static Task Remind(Neuron neuron, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(neuron);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return neuron.Remind(name, cancellationToken);
    }

    /// <summary>
    /// The single entry point a runtime calls before it creates a synapse: asks <paramref name="source"/> whether
    /// <paramref name="subscriber"/> may receive <paramref name="signals"/> from it.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The source refuses the subscriber.</exception>
    public static Task Admit(Neuron source, NeuronId subscriber, IReadOnlyList<Type> signals)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(signals);
        return source.Admit(new SynapseRequest(subscriber, signals));
    }

    /// <summary>
    /// The single entry point a runtime calls before it creates a synapse, deciding on <paramref name="request"/>.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The source refuses the subscriber.</exception>
    public static Task Admit(Neuron source, SynapseRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Signals);
        return source.Admit(request);
    }
}
