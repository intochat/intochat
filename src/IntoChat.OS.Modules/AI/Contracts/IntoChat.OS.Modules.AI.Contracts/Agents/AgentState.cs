using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.AI.Agents;

public enum AgentRunStatus { Running, Completed, Failed, Cancelled, Interrupted }

[GenerateSerializer, Alias("ai.agent-usage")]
public sealed record AgentUsage(
    [property: Id(0)] long? InputTokens,
    [property: Id(1)] long? OutputTokens,
    [property: Id(2)] long? TotalTokens);

// A tool call the run started: Result when it completed, Code and Error when it did not.
[GenerateSerializer, Alias("ai.agent-tool-call")]
public sealed record AgentToolCall(
    [property: Id(0)] string CallId,
    [property: Id(1)] string Name,
    [property: Id(2)] string Arguments,
    [property: Id(3)] string? Result = null,
    [property: Id(4)] string? Code = null,
    [property: Id(5)] string? Error = null);

// One turn of the agent, from the user's text to the reply or the reason there is none.
[GenerateSerializer, Alias("ai.agent-run")]
public sealed record AgentRun(
    [property: Id(0)] string RunId,
    [property: Id(1)] AgentRunStatus Status,
    [property: Id(2)] string UserText,
    [property: Id(3)] DateTimeOffset StartedAt,
    [property: Id(4)] DateTimeOffset? EndedAt = null,
    [property: Id(5)] string? Reply = null,
    [property: Id(6)] ResolvedModel? Model = null,
    [property: Id(7)] AgentUsage? Usage = null,
    [property: Id(8)] string? Error = null)
{
    [Id(9)] public AgentToolCall[] ToolCalls { get; init; } = [];
    // Recorded by IAgent.Post: an assistant-only entry with no user turn and no model call.
    [Id(10)] public bool Posted { get; init; }
}

// Transcript is read through IAgent.Transcript; it is too large to ride along on every read.
[GenerateSerializer, Alias("ai.agent-state")]
public sealed record AgentState(
    [property: Id(0)] long Revision,
    [property: Id(1)] AgentDefinition Definition,
    [property: Id(2)] string? Summary,
    [property: Id(3)] AgentRun? ActiveRun,
    [property: Id(4)] AgentRun[] Runs)
{
    // Revision guards configuration; Version orders every persisted conversation change.
    [Id(5)] public long Version { get; init; }
}
