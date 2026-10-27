using DigitalBrain.Confirmation;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Confirmation;

// The platform's person-facing confirmation contract. Applications may read pending requests but cannot create approvals.
[NeuronAlias("confirmations"), DefaultNeuronType("runtime.confirmations")]
public interface IConfirmationInbox
{
    static string Key(string brain) => brain + "/confirmations";

    Task<ConfirmationInbox> Read();
    Task Resolve(string planId, bool approve);
}

public sealed record ConfirmationInbox(long Revision, ConfirmationRequested[] Pending);
