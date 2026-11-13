using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Conversations;

public interface IConversationDomainService
{
    ConversationAggregate CreateConversation(string brainId, string? title = null, ConversationId? existingId = null);
    ConversationAggregate RestoreConversation(ConversationSnapshot snapshot);
    void ValidateBrainId(string brainId);
}
