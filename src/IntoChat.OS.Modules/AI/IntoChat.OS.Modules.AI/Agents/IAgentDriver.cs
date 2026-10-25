namespace IntoChat.OS.Modules.AI.Agents;

// Start hands the turn to this one-way self-reference, so the caller's request ends while the
// turn runs on the agent's own scheduler. Interleaving lets Read and Cancel reach the agent meanwhile.
[Alias("ai.agent-driver")]
internal interface IAgentDriver : IGrainWithStringKey
{
    [Orleans.Concurrency.OneWay, Orleans.Concurrency.AlwaysInterleave] Task Drive(AgentTurn turn, string runId);

    // Resume closes a left-open run during activation and announces it here, after activation. It
    // only publishes, so it interleaves rather than waiting behind a new turn.
    [Orleans.Concurrency.OneWay, Orleans.Concurrency.AlwaysInterleave] Task Interrupted(string runId, string error);
}
