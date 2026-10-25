using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework;

// Runs once per activation of TNeuron, in registration order, before the first call reaches it;
// a throw fails the activation. Modules register these instead of overriding the grain lifecycle.
public interface IActivationHandler<in TNeuron> : IActivationHandler where TNeuron : Neuron
{
    Task Activated(TNeuron neuron, CancellationToken cancellationToken);

    Task IActivationHandler.Activated(Neuron neuron, CancellationToken cancellationToken) => Activated((TNeuron)neuron, cancellationToken);
}

// The untyped form the runtime dispatches through; implement the generic one.
public interface IActivationHandler
{
    Task Activated(Neuron neuron, CancellationToken cancellationToken);
}
