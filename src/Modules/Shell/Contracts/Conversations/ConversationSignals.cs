using DigitalBrain;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.Flutter.Conversations;

[Signal("conversation.changed")]
public sealed record ConversationChanged(ConversationId Id, long Revision) : Signal;
