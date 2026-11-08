using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Approvals;

// The one approval list of a brain: a read adapter over the framework's confirmation inbox, not a store. Decide answers the inbox;
// whoever raised the plan (a flow, the synapse host, a storage gate) reacts to the resolution itself.
[NeuronAlias("approvals"), DefaultNeuronType("approvals")]
public interface IApprovals
{
    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "approvals", "approvals/index");

    [DigitalBrain.ReadOnlyCall]
    Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default);

    // The approver is the caller the runtime stamped; only a person may approve. Note is kept for the audit trail of later tasks.
    Task Decide(string planId, bool approve, string? note, CancellationToken ct = default);
}
