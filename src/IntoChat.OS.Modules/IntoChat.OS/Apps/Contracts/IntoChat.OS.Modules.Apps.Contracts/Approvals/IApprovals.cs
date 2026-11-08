using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Approvals;

// The one approval list of a brain: a read adapter over the framework's confirmation inbox, not a store, and read-only. Answering
// stays with the trusted edge: a view's approve and deny buttons target the framework's confirmations neuron
// (IConfirmations.ConfirmationsNeuronKey, IConfirmations.ResolveOperation) with Approval.PlanId. A neuron cannot vouch that an answer
// is a person's, so this neuron does not answer. Needs a trusted caller of the brain, as the inbox does.
[NeuronAlias("approvals"), DefaultNeuronType("approvals")]
public interface IApprovals
{
    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "approvals", "approvals/index");

    [DigitalBrain.ReadOnlyCall]
    Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default);
}
