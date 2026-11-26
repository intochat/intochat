using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Flows;

// The brain's door to engineering runs: it starts a flow.engineer run through the flows index, reads one as a summary, and passes
// a plan ratification or a candidate choice to the run. The flows index knows none of this; it only starts, lists and resumes flows.
// The commands interleave: they call the index and the run's flow neuron, and a run's step may be reading while they wait.
[NeuronAlias("engineer.runs"), DefaultNeuronType("engineer.runs")]
public interface IEngineerRuns
{
    const string DefaultKey = "engineer/runs";

    // The flow type of an engineering run across 1..N worktrees (EngineerFlow): the one flow type new engineering work starts.
    const string EngineerFlow = "flow.engineer";

    // The retired single-worktree flow type (FeatureToPrFlow). Nothing starts it; it stays hosted so a run already in flight finishes.
    const string ChangeFlow = "flow.change";

    // The durable PR review pipeline flow type (PrReviewFlow).
    const string PrReviewFlow = "flow.pr-review";

    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "engineer.runs", DefaultKey);

    [Interleaves]
    Task<EngineerRunSummary> StartEngineerRun(EngineerDelegationRequest request, CancellationToken ct = default);

    [ReadOnlyCall, Interleaves]
    Task<EngineerRunSummary> InspectEngineerRun(string? flowId = null, CancellationToken ct = default);

    [Interleaves]
    Task<EngineerRunSummary> SelectEngineerCandidate(string flowId, int candidateIndex, CancellationToken ct = default);

    [Interleaves]
    Task<EngineerRunSummary> RatifyEngineerPlan(
        string flowId,
        string[]? approvedCleanupTargets = null,
        EngineerTaskSlice[]? slices = null,
        CancellationToken ct = default);
}

// What the flow.engineer neuron offers beyond IFlowStarter. IEngineerRuns calls it; the flows index does not.
public interface IEngineerFlow
{
    Task SelectCandidate(int candidateIndex);

    Task RatifyPlan(
        string[]? approvedCleanupTargets = null,
        EngineerTaskSlice[]? slices = null,
        string? ratifiedBy = null);
}
