using DigitalBrain;

namespace IntoChat.OS.Modules.Flutter.Conversations;

[Signal("conversation.changed")]
public sealed record ConversationChanged(ConversationId Id, long Revision) : Signal
{
    /// <summary>
    /// Optional redacted summary of the human activity that triggered this change,
    /// consumed by read-model projections such as <see cref="ISessionList"/>.
    /// </summary>
    public string? ActivityText { get; init; }
}

[Signal("conversation.text-delta"), EphemeralSignal]
public sealed record TextDelta(ConversationId Id, EntryId Entry, string Delta) : Signal
{
    public int At { get; init; }
}
