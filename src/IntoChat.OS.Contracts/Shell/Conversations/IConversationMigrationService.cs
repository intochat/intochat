using DigitalBrain;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.AI.Conversations;

/// <summary>
/// Outcome of attempting to migrate a legacy <see cref="IAgent"/> into an <see cref="IConversation"/>.
/// </summary>
public enum ConversationMigrationStatus
{
    NothingToMigrate,
    Migrated,
    Failed,
}

/// <summary>
/// Normalized payload produced by <see cref="IConversationMigrationService"/> from legacy <see cref="IAgent"/> state.
/// </summary>
public sealed record ConversationMigrationResult(
    ConversationMigrationStatus Status,
    string? DefaultAgentId = null,
    AgentDefinition? DefaultAgentDefinition = null,
    ConversationEntry[]? Entries = null,
    AgentRun[]? Runs = null,
    bool Blocked = false)
{
    public static ConversationMigrationResult NothingToMigrate { get; } = new(ConversationMigrationStatus.NothingToMigrate);

    public static ConversationMigrationResult Failed { get; } = new(ConversationMigrationStatus.Failed);
}

/// <summary>
/// Application-layer service that reads legacy <see cref="IAgent"/> neurons and transforms
/// their state and transcript for import into <see cref="IConversation"/>.
/// </summary>
public interface IConversationMigrationService
{
    /// <summary>
    /// Reads the legacy <see cref="IAgent"/> associated with <paramref name="conversationId"/>
    /// and builds the normalized migration payload if legacy history exists.
    /// </summary>
    Task<ConversationMigrationResult> MigrateLegacyAgentAsync(
        NeuronId conversationId,
        AccountId ownerAccount,
        Func<NeuronId, IAgent>? resolveLegacyAgent = null,
        CancellationToken cancellationToken = default);
}
