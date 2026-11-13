using DigitalBrain.Signals;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Conversations;

[DigitalBrain.Signal("conversations.created")]
public sealed record ConversationCreated(ConversationId Id, string BrainId) : DigitalBrain.Signal;

[DigitalBrain.Signal("conversations.turn-recorded")]
public sealed record ConversationTurnRecorded(ConversationId Id, string TurnId, string Speaker) : DigitalBrain.Signal;

[DigitalBrain.Signal("conversations.run-started")]
public sealed record ConversationRunStarted(ConversationId Id, string RunId) : DigitalBrain.Signal;

[DigitalBrain.Signal("conversations.step-advanced")]
public sealed record ConversationRunStepAdvanced(ConversationId Id, string RunId, string Step) : DigitalBrain.Signal;

[DigitalBrain.Signal("conversations.run-completed")]
public sealed record ConversationRunCompleted(ConversationId Id, string RunId, ConversationRunStatus Status) : DigitalBrain.Signal;

[DigitalBrain.Signal("conversations.approval-changed")]
public sealed record ConversationApprovalChanged(ConversationId Id, string ApprovalId, ConversationApprovalStatus Status) : DigitalBrain.Signal;
