using System.Text.Json.Serialization;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Conversations;

[JsonConverter(typeof(JsonStringEnumConverter<ConversationApprovalKind>))]
public enum ConversationApprovalKind
{
    FlowStep,
    SynapseStaged,
    StorageWrite,
    Grant,
    Action,
    Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<ConversationApprovalStatus>))]
public enum ConversationApprovalStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Revoked,
}

public sealed record PendingApproval(
    string ApprovalId,
    ConversationId ConversationId,
    ConversationApprovalKind Kind,
    string Title,
    string Detail,
    ConversationApprovalStatus Status,
    DateTimeOffset RequestedAt,
    string? FlowId = null,
    string? Step = null,
    string? RunId = null,
    DateTimeOffset? ResolvedAt = null,
    string? ResolvedBy = null,
    string? ResolutionReason = null)
{
    public bool IsPending => Status == ConversationApprovalStatus.Pending;
    public bool IsResolved => Status is ConversationApprovalStatus.Approved or ConversationApprovalStatus.Denied or ConversationApprovalStatus.Expired or ConversationApprovalStatus.Revoked;
}
