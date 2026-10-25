using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Flutter.Chat;

// Holds input awaiting a behavior's acknowledgement, never conversation history.
[Alias("composer"), Orleans.Metadata.DefaultGrainType(UIVocabulary.ComposerType)]
public interface IComposer : INeuron
{
    Task Follow(string chat);
    Task Edit(string context, long selectionRevision, string value);
    Task<Guid?> Submit(Guid requestId, string context, long selectionRevision, string value);
    Task BeginVoice(Guid captureId, string context, long selectionRevision);
    Task Configure(string context, long generation, string label = "Message");
    Task Edit(string context, string value);
    Task<Guid?> Submit(Guid requestId, string context, string value);
    Task Settle(Guid requestId, string? error = null);
    Task Defer(Guid requestId, string error);
    Task BeginVoice(Guid captureId, string context);
    Task CaptureVoice(Guid captureId, byte[] audio, string mimeType);
    Task CancelVoice(Guid captureId);
    Task CompleteVoice(Guid captureId, string? text, string? error = null);
    [ReadOnly] Task<ComposerAudio?> ReadAudio(Guid captureId);
    [ReadOnly] Task<ComposerState> Read();
}

[GenerateSerializer, Alias("ui.composer-submission")]
public sealed record ComposerSubmission(
    [property: Id(0)] Guid Id,
    [property: Id(1)] string Context,
    [property: Id(2)] string Text,
    [property: Id(3)] long DraftVersion,
    [property: Id(4)] string Status = "pending",
    [property: Id(5)] string? Error = null,
    [property: Id(6)] long Generation = -1);

[GenerateSerializer, Alias("ui.composer-voice")]
public sealed record ComposerVoice(
    [property: Id(0)] Guid Id,
    [property: Id(1)] string Context,
    [property: Id(2)] long DraftVersion,
    [property: Id(3)] string Status,
    [property: Id(4)] string? Error = null);

[GenerateSerializer, Alias("ui.composer-audio")]
public sealed record ComposerAudio([property: Id(0)] byte[] Bytes, [property: Id(1)] string MimeType);

[GenerateSerializer, Alias("ui.composer-state")]
public sealed record ComposerState
{
    [Id(0)] public string Name { get; init; } = "";
    [Id(1)] public long Revision { get; init; }
    [Id(2)] public string Context { get; init; } = "";
    [Id(3)] public long Generation { get; init; } = -1;
    [Id(4)] public string Label { get; init; } = "Message";
    [Id(5)] public string Draft { get; init; } = "";
    [Id(6)] public long DraftVersion { get; init; }
    [Id(7)] public ComposerSubmission[] Submissions { get; init; } = [];
    [Id(8)] public string? Error { get; init; }
    [Id(9)] public ComposerVoice? Voice { get; init; }
    [Id(10)] public string? FollowedChat { get; init; }
}

[GenerateSerializer, Alias("ui.composer-changed")]
public sealed record ComposerChanged([property: Id(0)] string Name, [property: Id(1)] long Revision) : Signal;

[GenerateSerializer, Alias("ui.composer-submitted")]
public sealed record ComposerSubmitted([property: Id(0)] string Name, [property: Id(1)] Guid RequestId) : Signal;

[GenerateSerializer, Alias("ui.composer-voice-captured")]
public sealed record ComposerVoiceCaptured([property: Id(0)] string Name, [property: Id(1)] Guid CaptureId) : Signal;
