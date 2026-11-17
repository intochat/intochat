namespace DigitalBrain.Kernel;

/// <summary>
/// What a source's <c>AdmitSynapse</c> decides on (04-19): the subscriber and the signal types it asks for. The caller of the
/// asking call is the stamped caller the runtime propagates; it is never part of the request.
/// </summary>
/// <param name="Subscriber">The neuron that will hold the synapse.</param>
/// <param name="Signals">The signal types it asks for.</param>
public sealed record SynapseRequest(NeuronId Subscriber, IReadOnlyList<Type> Signals);
