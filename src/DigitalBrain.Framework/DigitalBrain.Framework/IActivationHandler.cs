namespace DigitalBrain.Framework;

/// <summary>
/// A step of the activation pipeline for one neuron type. Registered in DI by the module that
/// owns the neuron; the runtime runs every handler for the activating neuron's type, in
/// registration order, before the first call reaches it.
/// </summary>
/// <remarks>
/// Replaces overriding the grain lifecycle: modules say what happens on activation, the runtime
/// says when. A handler that throws fails the activation, as a lifecycle override would.
/// </remarks>
public interface IActivationHandler<in TNeuron> : IActivationHandler where TNeuron : Neuron
{
    Task Activated(TNeuron neuron, CancellationToken cancellationToken);

    Task IActivationHandler.Activated(Neuron neuron, CancellationToken cancellationToken) => Activated((TNeuron)neuron, cancellationToken);
}

/// <summary>The untyped form the runtime dispatches through; implement the generic one.</summary>
public interface IActivationHandler
{
    Task Activated(Neuron neuron, CancellationToken cancellationToken);
}
