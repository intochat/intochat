using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Approvals;

// The brain's approval list over the confirmation inbox. It keeps nothing: Pending reads the inbox and names what raised each
// plan (a waiting flow, a staged synapse, a storage action, a grant or other). It never answers a plan: the trusted edge does, and the raiser
// reacts to ConfirmationResolved itself. A staged synapse's plan is raised by the framework's installs neuron with the unit's
// alias as TargetLabel, so a staged source matches by its source key or by one of its aliases.
[DigitalBrain.Neuron("approvals"), StatelessNeuron]
public sealed class ApprovalsNeuron : DigitalBrain.Neuron, IApprovals
{
    private NeuronId InboxId => new(Id.Brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey);

    public async Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default)
    {
        var inbox = await Context.Get<IConfirmationInbox>(InboxId).Read().WaitAsync(ct);
        if (inbox.Pending.Length == 0) { return []; }
        var flowsRead = Context.Get<IFlows>(IFlows.Key(Id.Brain.Value)).List(new FlowQuery(Status: FlowStatus.Waiting), ct);
        var sourcesRead = Context.Get<ISynapseStudio>(ISynapseStudio.Key(Id.Brain.Value)).List();
        await Task.WhenAll(flowsRead, sourcesRead);
        var flows = await flowsRead;
        var staged = (await sourcesRead).Where(source => source.Status == DeployStatus.Staged).ToArray();
        return [.. inbox.Pending.Select(request => Classify(request, flows, staged)).OrderBy(approval => approval.Requested)];
    }

    private static Approval Classify(ConfirmationRequested request, FlowView[] flows, DeployedSource[] staged)
    {
        var plan = request.Plan;
        var requested = request.ExpiresAt - plan.Expiry;
        Approval Of(ApprovalKind kind, string subject, string title, string detail, string traceId)
            => new(request.PlanId, kind, subject, title, detail, requested, request.ExpiresAt, traceId);

        if (flows.FirstOrDefault(flow => flow.ConfirmationPlanId == request.PlanId) is { } flow)
        {
            return Of(ApprovalKind.FlowStep, flow.FlowId, flow.Title, $"{flow.WaitingStep}: {plan.Body}", flow.TraceId);
        }

        if (staged.FirstOrDefault(source => source.SourceKey == plan.TargetLabel || source.Aliases.Contains(plan.TargetLabel, StringComparer.Ordinal)) is { } unit)
        {
            return Of(ApprovalKind.SynapseStaged, unit.SourceKey, plan.Title, plan.Body, "");
        }

        if (plan.Action is ConfirmationAction.Delete or ConfirmationAction.Move or ConfirmationAction.Copy or ConfirmationAction.Rename or ConfirmationAction.Overwrite)
        {
            return Of(ApprovalKind.StorageWrite, plan.TargetLabel, plan.Title, plan.Body, "");
        }

        if (plan.TargetLabel.StartsWith("grant:", StringComparison.Ordinal))
        {
            return Of(ApprovalKind.Grant, plan.TargetLabel["grant:".Length..], plan.Title, plan.Body, "");
        }

        return Of(ApprovalKind.Other, plan.TargetLabel, plan.Title, plan.Body, "");
    }
}
