namespace DigitalBrain.AI.Agents.Signals;

// Sequence numbers a run's progress from 1; delivery is at-least-once, so handlers key their
// writes by (RunId, Sequence).
[GenerateSerializer, Alias("ai.agent-turn-progressed")]
public sealed record AgentTurnProgressed(
    [property: Id(0)] string RunId,
    [property: Id(1)] string Kind,
    [property: Id(2)] string? Text,
    [property: Id(3)] string? Tool,
    [property: Id(4)] string? Payload,
    [property: Id(5)] int Sequence) : Signal;

[GenerateSerializer, Alias("ai.agent-turn-completed")]
public sealed record AgentTurnCompleted([property: Id(0)] string RunId) : Signal;

[GenerateSerializer, Alias("ai.agent-turn-failed")]
public sealed record AgentTurnFailed([property: Id(0)] string RunId, [property: Id(1)] string Error) : Signal;
