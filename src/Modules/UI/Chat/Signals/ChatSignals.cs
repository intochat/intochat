using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Chat.Signals;

[Signal("ui.chat-thread-changed")]
public sealed record ChatThreadChanged(string ChatId, int Version, int MessageCount, bool Working) : Signal;

[Signal("ui.chat-submitted")]
public sealed record ChatMessageSubmitted(string ChatId, string Text, IReadOnlyList<ChatAttachment> Attachments) : Signal;

[Signal("ui.chat-stop")]
public sealed record ChatStopRequested(string ChatId) : Signal;

[Signal("ui.chat-retry")]
public sealed record ChatRetryRequested(string ChatId, string? MessageId) : Signal;

[Signal("ui.chat-compact")]
public sealed record ChatCompactRequested(string ChatId) : Signal;

[Signal("ui.chat-model-selected")]
public sealed record ChatModelSelected(string ChatId, string ModelId, bool MakeDefault = false) : Signal;
