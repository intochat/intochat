using DigitalBrain.Contracts;
using IntoChat.OS.Modules.AI.Inference;

namespace IntoChat.OS.Modules.AI.Agents;

[GenerateSerializer, Alias("ai.agent.compaction-result")]
public sealed record AgentCompactionResult
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public int PreviousMessageCount { get; init; }
    [Id(2)] public int KeptMessageCount { get; init; }
    [Id(3)] public string Summary { get; init; } = "";

    public AgentCompactionResult() { }

    public AgentCompactionResult(long revision, int previousMessageCount, int keptMessageCount, string summary)
    {
        Revision = revision;
        PreviousMessageCount = previousMessageCount;
        KeptMessageCount = keptMessageCount;
        Summary = summary ?? "";
    }
}

// The agent keeps the transcript and runs its own turn. Run streams the turn; Start answers the
// run id at once and reports the turn as signals. Both publish the same signals.
// A plain contract since 06-06d: every call interleaves, as it did on the grain (reads see only committed versions).
[NeuronAlias("agent"), DefaultNeuronType("agent")]
public interface IAgent
{
    [DigitalBrain.Contracts.Interleaves] Task Configure(AgentDefinition definition, long expectedRevision);
    [DigitalBrain.Contracts.Interleaves, DigitalBrain.Contracts.CallTimeout(600_000)] IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default);
    [DigitalBrain.Contracts.Interleaves] Task<string> Start(AgentTurn turn, string? flowId = null);
    [DigitalBrain.Contracts.Interleaves] Task Cancel();
    [DigitalBrain.Contracts.Interleaves] Task CancelRun(string runId);
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves] Task<AgentState> Read();
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves] Task<AiMessage[]> Transcript();
    [DigitalBrain.Contracts.Interleaves] Task Clear();
    [DigitalBrain.Contracts.Interleaves]
    Task<AgentCompactionResult> Compact(string? instructions = null)
        => throw new NotSupportedException("Compact is not supported by this agent implementation.");
    // Records a completed assistant-only entry (no model call), e.g. a greeting; one entry per id
    // (stored as run `post/{id}`). Throws InvalidOperationException while a run is active: retry later.
    [DigitalBrain.Contracts.Interleaves] Task Post(string text, string id);
}
