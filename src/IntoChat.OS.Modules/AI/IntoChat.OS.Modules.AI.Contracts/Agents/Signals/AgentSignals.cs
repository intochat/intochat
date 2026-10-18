using IntoChat.OS;

namespace IntoChat.OS.AI.Agents.Signals;

[GenerateSerializer, Alias("agent.configured")]
public sealed record AgentConfigured([property: Id(0)] string AgentId, [property: Id(1)] long Revision) : Signal;

[GenerateSerializer, Alias("agent.turn-started")]
public sealed record TurnStarted([property: Id(0)] string AgentId, [property: Id(1)] string RunId, [property: Id(2)] string UserText) : Signal
{
    // Persisted conversation version at publication; zero means no version was supplied.
    [Id(3)] public long Version { get; init; }
}

// Sequence numbers a run's progress from 1; delivery is at-least-once, so handlers key their
// writes by (RunId, Sequence). Kind is text, tool-call, tool-result, card or receipt. A text step
// holds the text streamed since the previous one (chunks are coalesced); in sequence they are the reply.
[GenerateSerializer, Alias("agent.turn-progressed")]
public sealed record TurnProgressed(
    [property: Id(0)] string AgentId,
    [property: Id(1)] string RunId,
    [property: Id(2)] string Kind,
    [property: Id(3)] string? Text,
    [property: Id(4)] string? Tool,
    [property: Id(5)] string? Payload,
    [property: Id(6)] int Sequence) : Signal
{
    [Id(7)] public long Version { get; init; }
}

[GenerateSerializer, Alias("agent.turn-completed")]
public sealed record TurnCompleted([property: Id(0)] string AgentId, [property: Id(1)] string RunId, [property: Id(2)] string Reply) : Signal
{
    [Id(3)] public long Version { get; init; }
}

[GenerateSerializer, Alias("agent.turn-failed")]
public sealed record TurnFailed([property: Id(0)] string AgentId, [property: Id(1)] string RunId, [property: Id(2)] string Error, [property: Id(3)] bool Cancelled) : Signal
{
    [Id(4)] public long Version { get; init; }
}

[GenerateSerializer, Alias("agent.transcript-cleared")]
public sealed record TranscriptCleared([property: Id(0)] string AgentId, [property: Id(1)] long Revision) : Signal;
