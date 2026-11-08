using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Approvals;

// The brain's approval list over the confirmation inbox. It keeps nothing: Pending reads the inbox and names what raised each
// plan (a waiting flow, a staged synapse, a storage action or a grant); Decide resolves the plan in the inbox and the raiser
// reacts to ConfirmationResolved itself. A staged synapse's plan is raised by the framework's installs neuron with the unit's
// alias as TargetLabel, so a staged source matches by its source key or by one of its aliases.
[DigitalBrain.Neuron("approvals"), StatelessNeuron]
public sealed class ApprovalsNeuron : DigitalBrain.Neuron, IApprovals
{
    private NeuronId InboxId => new(Id.Brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey);

    // The inbox only counts an answer as a person's when the call reaches it directly (not as a call from a neuron). A decision is
    // the caller's own, kernel-stamped and still checked by the inbox (user, no app, not on behalf of anyone), so it is forwarded
    // from the silo's services like the shell's confirmation endpoint does, not through a neuron-to-neuron call.
    private IConfirmationInbox DirectInbox => SiloNeurons.Get<IConfirmationInbox>(Context.Services, InboxId);

    public async Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default)
    {
        var inbox = await Context.Get<IConfirmationInbox>(InboxId).Read();
        if (inbox.Pending.Length == 0) { return []; }
        var flows = await Context.Get<IFlows>(IFlows.Key(Id.Brain.Value)).List(new FlowQuery(Status: FlowStatus.Waiting), ct);
        var staged = (await Context.Get<ISynapseStudio>(ISynapseStudio.Key(Id.Brain.Value)).List())
            .Where(source => source.Status == DeployStatus.Staged)
            .ToArray();
        return [.. inbox.Pending.Select(request => Classify(request, flows, staged)).OrderBy(approval => approval.Requested)];
    }

    public Task Decide(string planId, bool approve, string? note, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        return DirectInbox.Resolve(planId, approve);
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

        if (Names(plan.Title, "grant") || Names(plan.Body, "grant"))
        {
            return Of(ApprovalKind.Grant, plan.TargetLabel, plan.Title, plan.Body, "");
        }

        return Of(ApprovalKind.StorageWrite, plan.TargetLabel, plan.Title, plan.Body, "");
    }

    private static bool Names(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
