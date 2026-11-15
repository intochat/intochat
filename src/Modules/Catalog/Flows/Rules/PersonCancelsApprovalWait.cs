using DigitalBrain.Access;
using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows.Rules;

// A flow that waits for a person is cancelled by a person or the kernel, not by an agent or an app. The person need not be the
// direct caller: the owner's Cancel button arrives through the Tasks app. A person stamp with a conversation is the model.
internal sealed record PersonCancelsApprovalWait(string FlowId, string? WaitingStep, Caller? Caller) : Rule("apps.flows-person-cancels-approval-wait")
{
    public override bool Broken => WaitingStep is not null && Caller is not ({ Actor: Actor.Person, Conversation: null } or { Actor: Actor.Kernel });
    public override string Message => $"flow {FlowId} waits for a person at {WaitingStep}: only a person cancels it";
}
