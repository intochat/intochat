using DigitalBrain;
using Orleans.Concurrency;

namespace DigitalBrain.AI.Agents.Context;

// What an agent should know before it answers, as named slices each producer owns. The agent reads
// the latest snapshot at turn time; behaviors subscribe to the signals.
[Alias("agent-context"), Orleans.Metadata.DefaultGrainType("agent-context")]
public interface IAgentContext : INeuron
{
    Task Set(ContextEntry entry);
    Task Remove(string name);
    [ReadOnly, AlwaysInterleave] Task<ContextSnapshot> Read();
}

// Text is what the model should know; Tools are registered tool names it may need for it.
[GenerateSerializer, Alias("agent-context.entry")]
public sealed record ContextEntry([property: Id(0)] string Name, [property: Id(1)] string Text, [property: Id(2)] string[] Tools);

[GenerateSerializer, Alias("agent-context.snapshot")]
public sealed record ContextSnapshot([property: Id(0)] long Revision, [property: Id(1)] ContextEntry[] Entries);
