namespace IntoChat.OS.Modules.AI.Agents.Signals;

// Transitional (06-05b, deleted when AgentNeuron migrates): the agent is still a grain, and a plain neuron (a chat) follows it
// through a synapse. These [Signal] records carry the aliases of the agent's own signals, so the JSON the agent's signals are
// journalled with decodes for a plain subscriber; they keep only the agent id, all a follower needs to read the agent again.
[DigitalBrain.Signal("agent.configured")]
public sealed record AgentConfiguredChange(string AgentId) : DigitalBrain.Signal;

[DigitalBrain.Signal("agent.turn-started")]
public sealed record AgentTurnStartedChange(string AgentId) : DigitalBrain.Signal;

[DigitalBrain.Signal("agent.turn-progressed")]
public sealed record AgentTurnProgressedChange(string AgentId) : DigitalBrain.Signal;

[DigitalBrain.Signal("agent.turn-completed")]
public sealed record AgentTurnCompletedChange(string AgentId) : DigitalBrain.Signal;

[DigitalBrain.Signal("agent.turn-failed")]
public sealed record AgentTurnFailedChange(string AgentId) : DigitalBrain.Signal;

[DigitalBrain.Signal("agent.transcript-cleared")]
public sealed record AgentTranscriptClearedChange(string AgentId) : DigitalBrain.Signal;

public static class AgentChanges
{
    // Every signal an agent publishes, as its plain change record.
    public static Type[] Signals { get; } =
    [
        typeof(AgentConfiguredChange), typeof(AgentTurnStartedChange), typeof(AgentTurnProgressedChange),
        typeof(AgentTurnCompletedChange), typeof(AgentTurnFailedChange), typeof(AgentTranscriptClearedChange),
    ];

    // The agent neuron's type.
    public const string NeuronType = "agent";
}
