using DigitalBrain.Brains;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Conversations;

public sealed class ConversationDomainService : IConversationDomainService
{
    public ConversationAggregate CreateConversation(string brainId, string? title = null, ConversationId? existingId = null)
    {
        ValidateBrainId(brainId);

        var id = existingId is { } provided && !string.IsNullOrWhiteSpace(provided.Value)
            ? (provided.IsCanonical ? provided : (ConversationId.TryParse(provided.Value, out var parsed) ? parsed : ConversationId.New()))
            : ConversationId.New();

        return ConversationAggregate.Create(brainId, id, title);
    }

    public ConversationAggregate RestoreConversation(ConversationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateBrainId(snapshot.BrainId);
        return ConversationAggregate.Restore(snapshot);
    }

    public void ValidateBrainId(string brainId)
    {
        if (string.IsNullOrWhiteSpace(brainId) || !BrainScope.IsValidId(brainId))
        {
            throw new ArgumentException($"Invalid or non-local brain id '{brainId}'.", nameof(brainId));
        }
    }
}
