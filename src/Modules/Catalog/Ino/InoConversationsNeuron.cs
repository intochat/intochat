namespace IntoChat.OS.Modules.Apps;

// Legacy read-only neuron kept for two releases after S3 for SessionListNeuron's lazy one-time import. See OWED.md.
[DigitalBrain.Neurons.Neuron("ino.conversations")]
internal sealed class InoConversationsNeuron : DigitalBrain.Neurons.Neuron<InoConversationsState>, IInoConversations
{
    public Task<string[]> List() => Task.FromResult(State.Agents);
}
