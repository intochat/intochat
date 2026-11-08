using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Approvals;

// The brain's approval list over the confirmation inbox. It keeps nothing: Pending reads the inbox and names what raised each
// plan (a waiting flow, a staged synapse, a storage action, a grant or other). It never answers a plan: the trusted edge does, and the raiser
// reacts to ConfirmationResolved itself. A staged synapse's plan is raised by the framework's installs neuron with the unit's
// alias as TargetLabel, so a staged source matches by its source key or by one of its aliases; a build flow waiting on that ask makes
// it SynapseStaged with the needs sentence and hosts, and a build flow waiting for a connection is an Other card of its own (no plan).
[DigitalBrain.Neuron("approvals"), StatelessNeuron]
public sealed class ApprovalsNeuron : DigitalBrain.Neuron, IApprovals
{
    private NeuronId InboxId => new(Id.Brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey);

    public async Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default)
    {
        var inboxRead = Context.Get<IConfirmationInbox>(InboxId).Read().WaitAsync(ct);
        var flowsRead = Context.Get<IFlows>(IFlows.Key(Id.Brain.Value)).List(new FlowQuery(Status: FlowStatus.Waiting), ct);
        await Task.WhenAll(inboxRead, flowsRead);
        var inbox = await inboxRead;
        var flows = await flowsRead;
        // A waiting build flow's own state names the needs it asks (install) or the connection it waits for (deploy).
        var builds = await Task.WhenAll(flows.Where(flow => flow.Type == BuildFlow.Alias && flow.WaitingFor == WaitReason.External)
            .Select(async flow => (Flow: flow, Inner: await BuildOf(flow.FlowId))));
        var connect = builds.Where(build => build.Flow.WaitingStep == "deploy" && build.Inner?.Missing is not null)
            .Select(build => new Approval(build.Flow.FlowId, ApprovalKind.Other, build.Flow.FlowId, build.Inner!.Missing!,
                $"{build.Flow.Title}: {build.Inner.Missing}", build.Flow.Started, DateTimeOffset.MaxValue, build.Flow.TraceId)).ToArray();
        if (inbox.Pending.Length == 0) { return [.. connect.OrderBy(approval => approval.Requested)]; }
        var staged = (await Context.Get<ISynapseStudio>(ISynapseStudio.Key(Id.Brain.Value)).List()).Where(source => source.Status == DeployStatus.Staged).ToArray();
        var installs = builds.Where(build => build.Flow.WaitingStep == "install" && build.Inner?.PlanId is not null).ToArray();
        return [.. inbox.Pending.Select(request => Classify(request, flows, staged, installs)).Concat(connect).OrderBy(approval => approval.Requested)];
    }

    private async Task<BuildInner?> BuildOf(string flowId)
    {
        if (!NeuronId.TryParse(flowId, out var id)) { return null; }
        try { return JsonSerializer.Deserialize<BuildInner>((await Context.Get<IFlowStarter>(id).Head()).InnerJson, BuildFlow.Json); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    private static Approval Classify(ConfirmationRequested request, FlowView[] flows, DeployedSource[] staged, (FlowView Flow, BuildInner? Inner)[] installs)
    {
        var plan = request.Plan;
        var requested = request.ExpiresAt - plan.Expiry;
        Approval Of(ApprovalKind kind, string subject, string title, string detail, string traceId)
            => new(request.PlanId, kind, subject, title, detail, requested, request.ExpiresAt, traceId);

        if (flows.FirstOrDefault(flow => flow.ConfirmationPlanId == request.PlanId) is { } flow)
        {
            return Of(ApprovalKind.FlowStep, flow.FlowId, flow.Title, $"{flow.WaitingStep}: {plan.Body}", flow.TraceId);
        }

        // The installs neuron's ask for a unit a build flow deployed: the needs sentence and the hosts its slots read.
        if (installs.Where(build => build.Inner?.PlanId == request.PlanId).Select(build => (build.Flow, Inner: build.Inner!)).ToArray() is [var install, ..])
        {
            return Of(ApprovalKind.SynapseStaged, install.Inner.Alias ?? plan.TargetLabel, plan.Title, NeedsDetail(install.Inner, plan.Body), install.Flow.TraceId);
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

    // "{sentence} Reads api.x.com." from the unit's needs; the plan body when the unit names no hosts.
    private static string NeedsDetail(BuildInner inner, string body)
    {
        var sentence = string.IsNullOrWhiteSpace(inner.Sentence) ? body : inner.Sentence;
        string[] hosts;
        try { hosts = [.. BuildFlow.UnitOf(inner).Needs.Slots.SelectMany(slot => slot.Hosts).Distinct(StringComparer.OrdinalIgnoreCase)]; }
        catch (Exception error) when (error is JsonException or FlowFailed) { hosts = []; }
        return hosts.Length == 0 ? sentence : $"{sentence} Reads {string.Join(", ", hosts)}.";
    }
}
