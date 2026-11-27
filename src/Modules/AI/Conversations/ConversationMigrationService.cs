using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Conversations;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.AI.Conversations;

internal sealed class ConversationMigrationService(
    ConversationDefaults? defaults = null,
    INeuronProxies? proxies = null,
    ILogger<ConversationMigrationService>? logger = null) : IConversationMigrationService
{
    internal const string ConversationsKeyPrefix = "conversations/";

    private ConversationDefaults Defaults => defaults ?? ConversationDefaults.Generic;
    private ILogger Logger => (ILogger?)logger ?? OsConsoleLog.For<ConversationMigrationService>();

    public async Task<ConversationMigrationResult> MigrateLegacyAgentAsync(
        NeuronId conversationId,
        AccountId ownerAccount,
        Func<NeuronId, IAgent>? resolveLegacyAgent = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!conversationId.Key.StartsWith(ConversationsKeyPrefix, StringComparison.Ordinal))
        {
            return ConversationMigrationResult.NothingToMigrate;
        }

        var legacyPrefix = Defaults.LegacyAgentKeyPrefix;
        if (string.IsNullOrWhiteSpace(legacyPrefix))
        {
            return ConversationMigrationResult.NothingToMigrate;
        }

        var rawId = conversationId.Key[ConversationsKeyPrefix.Length..];
        var legacyId = new NeuronId(conversationId.Brain, AgentSignals.NeuronType, legacyPrefix + rawId);
        var oldAgent = resolveLegacyAgent is not null
            ? resolveLegacyAgent(legacyId)
            : proxies?.Get<IAgent>(legacyId);
        if (oldAgent is null)
        {
            return ConversationMigrationResult.NothingToMigrate;
        }

        try
        {
            var oldState = await oldAgent.Read();
            if (oldState.Revision == 0 && oldState.Runs.Length == 0 && string.IsNullOrWhiteSpace(oldState.Summary))
            {
                return ConversationMigrationResult.NothingToMigrate;
            }

            var transcript = oldState.Runs.Length > 0 ? await oldAgent.Transcript() : [];
            var defaultAgentId = Defaults.DefaultAgentId;
            var (importedEntries, importedRuns) = LegacyImport.From(oldState, transcript, ownerAccount, defaultAgentId);

            return new ConversationMigrationResult(
                ConversationMigrationStatus.Migrated,
                defaultAgentId,
                AgentDefinitionRules.Normalize(oldState.Definition),
                importedEntries,
                importedRuns,
                oldState.Blocked);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Logger.LogWarning(error, "Legacy import for conversation {ConversationId} failed; will retry on next call.", conversationId.Key);
            return ConversationMigrationResult.Failed;
        }
    }

    internal static ConversationState ApplyToState(
        ConversationState current,
        ConversationMigrationResult migrated,
        AccountId ownerAccount,
        string fallbackAgentId,
        int keptRuns)
    {
        var agentId = migrated.DefaultAgentId ?? fallbackAgentId;
        var nextAgents = current.Agents.Any(a => string.Equals(a.AgentId, agentId, StringComparison.Ordinal))
            ? current.Agents
            : [.. current.Agents, new SeatedAgent(agentId, migrated.DefaultAgentDefinition ?? new AgentDefinition())];

        var importedEntries = migrated.Entries ?? [];
        var importedRuns = migrated.Runs ?? [];
        var participants = new List<Participant>(current.Participants);
        Participant human = new Participant.Human(ownerAccount);
        Participant assistant = new Participant.Assistant(agentId);
        if (!participants.Contains(human)) { participants.Add(human); }
        if (!participants.Contains(assistant)) { participants.Add(assistant); }

        var nextLog = importedEntries.Length > 0
            ? ConversationLog.Append(current.Log, current.Revision + 1, importedEntries)
            : current.Log;
        var nextRuns = importedRuns.Length > keptRuns
            ? importedRuns[(importedRuns.Length - keptRuns)..]
            : importedRuns;

        return current with
        {
            Participants = [.. participants],
            Agents = nextAgents,
            Log = nextLog,
            Runs = nextRuns,
            Imported = true,
        };
    }
}
