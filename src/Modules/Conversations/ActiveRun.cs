using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Conversations;

[JsonConverter(typeof(JsonStringEnumConverter<ConversationRunStatus>))]
public enum ConversationRunStatus
{
    Pending,
    Running,
    WaitingApproval,
    Completed,
    Failed,
    Cancelled,
}

public sealed record ActiveRun(
    string RunId,
    ConversationRunStatus Status,
    DateTimeOffset StartedAt,
    string? FlowId = null,
    string? Step = null,
    string? Prompt = null,
    string? Reply = null,
    string? Error = null,
    DateTimeOffset? EndedAt = null)
{
    public bool IsActive => Status is ConversationRunStatus.Pending or ConversationRunStatus.Running or ConversationRunStatus.WaitingApproval;
    public bool IsTerminal => Status is ConversationRunStatus.Completed or ConversationRunStatus.Failed or ConversationRunStatus.Cancelled;
}
