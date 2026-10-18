using Orleans.Concurrency;

namespace DigitalBrain.Flutter.Chat;

// A recoverable UI projection. The source neuron owns the conversation and runs its turns.
[Alias("chat"), Orleans.Metadata.DefaultGrainType(UIVocabulary.ChatType)]
public interface IChat : INeuron
{
    Task<ChatSelection> SelectAgent(string agent, long expectedSelectionRevision, Guid operationId);
    Task Project(ChatProjection projection);
    // IDs identify immutable intents within a generation; use a new ID when the action target changes.
    Task Activate(string source, long generation, string actionId);
    Task MarkRead(string source, long generation, long version, string[] messageIds);
    [ReadOnly] Task<ChatState> Read();
}

[GenerateSerializer, Alias("ui.chat-projection")]
public sealed record ChatProjection(
    [property: Id(0)] string Source,
    [property: Id(1)] long Generation,
    [property: Id(2)] long Version,
    [property: Id(3)] ChatEntry[] Messages)
{
    [Id(4)] public ChatAction[] Actions { get; init; } = [];
}

[GenerateSerializer, Alias("ui.chat-action")]
public sealed record ChatAction(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] string Payload,
    [property: Id(3)] bool Enabled = true);

[GenerateSerializer, Alias("ui.chat-action-activated")]
public sealed record ChatActionActivated(
    [property: Id(0)] string Name,
    [property: Id(1)] string Source,
    [property: Id(2)] long Generation,
    [property: Id(3)] string ActionId,
    [property: Id(4)] string Payload,
    [property: Id(5)] Guid RequestId) : Signal;

[GenerateSerializer, Alias("ui.chat-state")]
public sealed record ChatState
{
    [Id(0)] public string Name { get; init; } = "";
    [Id(1)] public long Revision { get; init; }
    [Id(2)] public ChatProjection? Projection { get; init; }
    [Id(3)] public ChatReadReceipt[] ReadReceipts { get; init; } = [];
    [Id(4)] public string[] UnreadMessageIds { get; init; } = [];
    [Id(5)] public ChatSelection? Selection { get; init; }
    [Id(6)] public ChatRunSnapshot? Run { get; init; }
    [Id(7)] public long ActivityRevision { get; init; }
    // Receipts are persisted with selection, but never exposed by Read.
    [Id(8)] public ChatSelectionReceipt[] SelectionReceipts { get; init; } = [];
}

[GenerateSerializer, Alias("ui.chat-selection")]
public sealed record ChatSelection([property: Id(0)] string Agent, [property: Id(1)] long Revision);

[GenerateSerializer, Alias("ui.chat-selection-receipt")]
public sealed record ChatSelectionReceipt([property: Id(0)] Guid OperationId, [property: Id(1)] ChatSelection Selection);

[GenerateSerializer, Alias("ui.chat-run-status")]
public enum ChatRunStatus { Running, Completed, Failed, Cancelled, Interrupted }

[GenerateSerializer, Alias("ui.chat-run-snapshot")]
public sealed record ChatRunSnapshot(
    [property: Id(0)] string Source, [property: Id(1)] long SelectionRevision,
    [property: Id(2)] long Version, [property: Id(3)] string RunId, [property: Id(4)] ChatRunStatus Status);

[GenerateSerializer, Alias("ui.chat-selection-changed")]
public sealed record ChatSelectionChanged([property: Id(0)] string Name, [property: Id(1)] ChatSelection Selection) : Signal;

[GenerateSerializer, Alias("ui.chat-run-changed")]
public sealed record ChatRunChanged(
    [property: Id(0)] string Name,
    [property: Id(1)] long ActivityRevision,
    [property: Id(2)] ChatRunStatus? Status = null) : Signal;

[GenerateSerializer, Alias("ui.chat-read-receipt")]
public sealed record ChatReadReceipt(
    [property: Id(0)] string PrincipalId,
    [property: Id(1)] string Source,
    [property: Id(2)] string MessageId,
    [property: Id(3)] string ContentHash);

[GenerateSerializer, Alias("ui.chat-changed")]
public sealed record ChatChanged([property: Id(0)] string Name, [property: Id(1)] long Revision) : Signal;
