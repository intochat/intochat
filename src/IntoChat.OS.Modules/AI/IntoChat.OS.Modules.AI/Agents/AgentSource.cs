namespace IntoChat.OS.Modules.AI.Agents;

// Transitional (06-05b, R4-13; deleted when AgentNeuron migrates): the runtime admits a synapse only through the plain class
// hosted for the source's type, and the agent is still a grain. This stand-in admits subscribers of the agent's own brain (the
// runtime default) and serves nothing else; the agent's signals still come from the grain through the shared BrainLog.
[DigitalBrain.Neuron(Signals.AgentChanges.NeuronType)]
internal sealed class AgentSource : DigitalBrain.Neuron;
