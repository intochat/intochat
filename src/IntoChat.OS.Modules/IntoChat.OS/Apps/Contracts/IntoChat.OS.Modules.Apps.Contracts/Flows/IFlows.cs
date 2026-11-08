using DigitalBrain.Contracts;
using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps.Flows;

// The brain's index of flows: it folds every flow.* signal of the brain into a view and drives flows by id. A flow type is an
// alias whose neuron implements IFlowStarter; Start keys the new flow {brain}/{type}/{Guid "N"} and returns that id.
// The commands interleave: they only call flow neurons, so a flow step may call the index while the index waits on a flow.
[NeuronAlias("flows"), DefaultNeuronType("flows")]
public interface IFlows
{
    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "flows", "flows/index");

    [DigitalBrain.ReadOnlyCall]
    Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default);

    [DigitalBrain.ReadOnlyCall]
    Task<FlowView?> Read(string flowId, CancellationToken ct = default);

    // Refused (rule apps.flows-parent-not-blocked, "blocked by {targetId}") while the parent flow is blocked.
    [DigitalBrain.Interleaves]
    Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default);

    [DigitalBrain.Interleaves]
    Task Resume(string flowId, string step, WaitOutcome outcome, CancellationToken ct = default);

    // Cancels the running step's leaf operation, then the flow.
    [DigitalBrain.Interleaves]
    Task Cancel(string flowId, string reason, CancellationToken ct = default);

    [DigitalBrain.Interleaves]
    Task Relate(string flowId, FlowRelation relation, CancellationToken ct = default);
}
