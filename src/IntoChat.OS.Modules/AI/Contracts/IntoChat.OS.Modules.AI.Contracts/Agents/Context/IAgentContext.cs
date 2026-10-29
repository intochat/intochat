using DigitalBrain;
using DigitalBrain.Contracts;
using IntoChat.OS;

namespace IntoChat.OS.Modules.AI.Agents.Context;

// What an agent should know before it answers, as named slices each producer owns. The agent reads
// the latest snapshot at turn time; behaviors subscribe to the signals.
[NeuronAlias("agent-context"), DefaultNeuronType("agent-context")]
public interface IAgentContext
{
    Task Set(ContextEntry entry);
    Task Remove(string name);
    [DigitalBrain.ReadOnlyCall, DigitalBrain.Interleaves] Task<ContextSnapshot> Read();
}

// Text is what the model should know; Contracts are full type names of neuron contracts it may need for it.
public sealed record ContextEntry(string Name, string Text, string[] Contracts);

public sealed record ContextSnapshot(long Revision, ContextEntry[] Entries);
