using DigitalBrain;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.Flutter.Conversations;

[Signal("conversation.changed")]
public sealed record ConversationChanged(ConversationId Id, long Revision) : Signal;

[Signal("conversation.text-delta"), EphemeralSignal]
public sealed record TextDelta(ConversationId Id, EntryId Entry, string Delta) : Signal
{
    public int At { get; init; }
}
