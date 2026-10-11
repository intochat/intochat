namespace DigitalBrain.Framework;

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

// Task 22 (R101): an activation made only to forward its calls to the machine that holds its part (a stand-in) runs
// no activation handler: it read no state and must neither publish nor act. The machine runtime provides it.
public interface IStandInActivations
{
    bool IsStandIn(GrainId grain);
}
