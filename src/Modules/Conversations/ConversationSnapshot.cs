using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Conversations;

public sealed record ConversationSnapshot(
    ConversationId Id,
    string BrainId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConversationTurn> Turns,
    ActiveRun? ActiveRun,
    IReadOnlyList<ActiveRun> RunHistory,
    IReadOnlyList<PendingApproval> Approvals);
