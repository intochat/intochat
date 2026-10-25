namespace IntoChat.OS.Modules.Flutter.Chat;

// Composition seam: the UI runtime need not reference AI implementation/contracts.
// Implementations resolve one authoritative agent and translate its recoverable snapshot.
public interface IChatAgentSource
{
    INeuron Source(string key);
    Task<ChatAgentSnapshot> Read(string key, long selectionRevision);
}

public sealed record ChatAgentSnapshot(ChatProjection Projection, ChatRunSnapshot? Run);

// Infrastructure only: neither interface is a neuron or exposed to scripts/model tools.
[Alias("ui.chat-migration"), Orleans.Metadata.DefaultGrainType(UIVocabulary.ChatType)]
public interface IChatMigration : IGrainWithStringKey
{
    Task AdoptAgent(string agent, long generation, Guid operationId);
}

[Alias("ui.chat-reconciliation"), Orleans.Metadata.DefaultGrainType(UIVocabulary.ChatType)]
internal interface IChatReconciliation : IGrainWithStringKey
{
    [Orleans.Concurrency.OneWay] Task Reconcile();
}
