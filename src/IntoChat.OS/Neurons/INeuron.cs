namespace IntoChat.OS;

/// <summary>
/// A marker the os vocabulary still names for contracts reached by reflection (the neuron surface, the contract catalog).
/// Neurons are DigitalBrain plain neurons (06-08): watching is a synapse on the runtime, so this contract has no members.
/// </summary>
[Alias("kernel.neuron")]
public interface INeuron : IGrainWithStringKey;
