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
[GenerateSerializer, Alias("agent-context.entry")]
public sealed record ContextEntry([property: Id(0)] string Name, [property: Id(1)] string Text, [property: Id(2)] string[] Contracts);

[GenerateSerializer, Alias("agent-context.snapshot")]
public sealed record ContextSnapshot([property: Id(0)] long Revision, [property: Id(1)] ContextEntry[] Entries);
