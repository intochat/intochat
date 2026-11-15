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

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<FlowView[]> List(FlowQuery query, CancellationToken ct = default);

    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<FlowView?> Read(string flowId, CancellationToken ct = default);

    // Refused (rule apps.flows-parent-not-blocked, "blocked by {targetId}") while the parent flow is blocked. FlowId, when given, is
    // the new flow's key (non-empty, no '/'): the flow is {brain}/{type}/{flowId}, and a key already started is refused (rule
    // apps.flows-flow-is-new, "flow exists: {id}").
    [DigitalBrain.Contracts.Interleaves]
    Task<string> Start(string type, string title, string? parentFlowId, string innerJson, CancellationToken ct = default, string? flowId = null);

    [DigitalBrain.Contracts.Interleaves]
    Task Resume(string flowId, string step, WaitOutcome outcome, CancellationToken ct = default);

    // Cancels the running step's leaf operation, then the flow. Refused (rule apps.flows-person-cancels-approval-wait, "flow {id}
    // waits for a person at {step}: only a person cancels it") while the flow waits for a person's approval, unless the caller is
    // a person or the kernel. The person may call through an app's neuron; an agent, an app and a person stamp that carries a
    // conversation are refused, and nothing is cancelled. A waiting flow whose wait the index has not folded yet counts as
    // waiting for a person.
    [DigitalBrain.Contracts.Interleaves]
    Task Cancel(string flowId, string reason, CancellationToken ct = default);

    [DigitalBrain.Contracts.Interleaves]
    Task Relate(string flowId, FlowRelation relation, CancellationToken ct = default);
}
