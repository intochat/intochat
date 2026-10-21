using IntoChat.OS;
using IntoChat.OS.AI.Inference;
using Orleans.Concurrency;

namespace IntoChat.OS.AI.Agents;

// The agent keeps the transcript and runs its own turn. Run streams the turn; Start answers the
// run id at once and reports the turn as signals. Both publish the same signals.
[Alias("agent"), Orleans.Metadata.DefaultGrainType("agent")]
public interface IAgent : INeuron
{
    [AlwaysInterleave] Task Configure(AgentDefinition definition, long expectedRevision);
    [AlwaysInterleave, ResponseTimeout("00:10:00")] IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default);
    [AlwaysInterleave] Task<string> Start(AgentTurn turn);
    [AlwaysInterleave] Task Cancel();
    [AlwaysInterleave] Task CancelRun(string runId);
    [ReadOnly, AlwaysInterleave] Task<AgentState> Read();
    [ReadOnly, AlwaysInterleave] Task<AiMessage[]> Transcript();
    [AlwaysInterleave] Task Clear();
    // Records a completed assistant-only entry (no model call), e.g. a greeting; one entry per id
    // (stored as run `post/{id}`). Throws InvalidOperationException while a run is active: retry later.
    [AlwaysInterleave] Task Post(string text, string id);
}
