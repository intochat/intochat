using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Log;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Approvals;

// The brain's approval list over the confirmation inbox. It keeps nothing: Pending reads the inbox and names what raised each
// plan (a waiting flow, a staged synapse, a storage action, a grant or other). It never answers a plan: the trusted edge does, and the raiser
// reacts to ConfirmationResolved itself. A staged synapse's plan is raised by the framework's installs neuron with the unit's
// alias as TargetLabel, so a staged source matches by its source key or by one of its aliases; a build flow waiting on that ask makes
// it SynapseStaged with the needs sentence and hosts, and a build flow waiting for a connection is an Other card of its own (no plan).
[Neuron("approvals"), StatelessNeuron]
public sealed class ApprovalsNeuron : Neuron, IApprovals
{
    private NeuronId InboxId => new(Id.Brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey);

    public async Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default)
    {
        RequireSameBrain(() => new UnauthorizedAccessException("Only signed-in callers within the same brain can read pending approvals."));
        var inboxRead = Context.Get<IConfirmationInbox>(InboxId).Read().WaitAsync(ct);
        var flowsRead = Context.Get<IFlows>(IFlows.Key(Id.Brain.Value)).List(new FlowQuery(Status: FlowStatus.Waiting), ct);
        await Task.WhenAll(inboxRead, flowsRead);
        var inbox = await inboxRead;
        var flows = await flowsRead;
        // A waiting build flow's own state names the needs it asks (install) or the connection it waits for (deploy).
        var builds = await Task.WhenAll(flows.Where(flow => flow.Type == BuildInner.Flow && flow.WaitingFor == WaitReason.External)
            .Select(async flow => (Flow: flow, Inner: await BuildOf(flow.FlowId))));
        var connect = builds.Where(build => build.Flow.WaitingStep == "deploy" && build.Inner?.Missing is not null)
            .Select(build => new Approval(build.Flow.FlowId, ApprovalKind.Other, build.Flow.FlowId, build.Inner!.Missing!,
                $"{build.Flow.Title}: {build.Inner.Missing}", build.Flow.Started, DateTimeOffset.MaxValue, build.Flow.TraceId)).ToArray();
        if (inbox.Pending.Length == 0) { return [.. connect.DistinctBy(approval => approval.PlanId).OrderBy(approval => approval.Requested)]; }
        var staged = (await Context.Get<ISynapseStudio>(ISynapseStudio.Key(Id.Brain.Value)).List()).Where(source => source.Status == DeployStatus.Staged).ToArray();
        var inners = builds.Where(build => build.Inner is not null).ToDictionary(build => build.Flow.FlowId, build => build.Inner!, StringComparer.Ordinal);
        var now = Context.Time.GetUtcNow();
        var classified = inbox.Pending
            .Where(request => request.ExpiresAt > now)
            .Select(request => Classify(request, flows, staged, inners))
            .OfType<Approval>();
        return [.. classified.Concat(connect).DistinctBy(approval => approval.PlanId).OrderBy(approval => approval.Requested)];
    }

    private async Task<BuildInner?> BuildOf(string flowId)
    {
        if (!NeuronId.TryParse(flowId, out var id)) { return null; }
        try { return JsonSerializer.Deserialize<BuildInner>((await Context.Get<IFlowStarter>(id).Head()).InnerJson, BuildInner.Json); }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            OsConsoleLog.For<ApprovalsNeuron>().LogSwallowed(error, "reading optional build flow inner state");
            return null;
        }
    }

    // DigitalBrain.InstallsNeuron names each install ask's plan "install:{alias}:{id}".
    private const string InstallPlanPrefix = "install:";

    private static Approval? Classify(ConfirmationRequested request, FlowView[] flows, DeployedSource[] staged, Dictionary<string, BuildInner> builds)
    {
        var plan = request.Plan;
        var requested = request.ExpiresAt - plan.Expiry;
        Approval Of(ApprovalKind kind, string subject, string title, string detail, string traceId)
            => new(request.PlanId, kind, subject, title, detail, requested, request.ExpiresAt, traceId);

        // A build flow's install wait names the installs neuron's ask as its plan: that is consent to the unit's needs, not a flow
        // step, so it is checked before the FlowStep match. Its detail is the needs sentence and the hosts the slots read.
        if (flows.FirstOrDefault(flow => flow.ConfirmationPlanId == request.PlanId) is { } flow)
        {
            if (flow is { Type: BuildInner.Flow, WaitingStep: "install" })
            {
                var inner = builds.GetValueOrDefault(flow.FlowId);
                return Of(ApprovalKind.SynapseStaged, inner?.Alias ?? plan.TargetLabel, plan.Title, inner is null ? plan.Body : NeedsDetail(inner, plan.Body), flow.TraceId);
            }
            return Of(ApprovalKind.FlowStep, flow.FlowId, flow.Title, $"{flow.WaitingStep}: {plan.Body}", flow.TraceId);
        }

        // A build flow's choice of connection: one plan per candidate, each a step of that flow.
        if (builds.FirstOrDefault(build => build.Value.ChoicePlans?.ContainsKey(request.PlanId) == true) is { Key: { } chooser, Value: { } choosingInner }
            && flows.FirstOrDefault(flow => flow.FlowId == chooser) is { } choosing)
        {
            var choice = choosingInner.ChoicePlans?[request.PlanId];
            if (choice is { Length: > 0 } && choosingInner.Chosen?.ContainsKey(choice[0]) == true)
            {
                // Slot was already chosen; candidate plan is resolved/withdrawn.
                return null;
            }
            return Of(ApprovalKind.FlowStep, choosing.FlowId, plan.Title, $"{choosing.WaitingStep}: {plan.Body}", choosing.TraceId);
        }

        if (staged.FirstOrDefault(source => source.SourceKey == plan.TargetLabel || source.Aliases.Contains(plan.TargetLabel, StringComparer.Ordinal)) is { } unit)
        {
            return Of(ApprovalKind.SynapseStaged, unit.SourceKey, plan.Title, plan.Body, "");
        }

        // Any install ask the framework's installs neuron raises (plan "install:{alias}:{id}", its body the needs sentence) is consent
        // to a synapse unit's needs, also when no staged source or build flow is behind it: a shipped unit's install reads so too.
        if (request.PlanId.StartsWith(InstallPlanPrefix, StringComparison.Ordinal))
        {
            return Of(ApprovalKind.SynapseStaged, plan.TargetLabel, plan.Title, plan.Body, "");
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
        try { hosts = [.. inner.Unit().Needs.Slots.SelectMany(slot => slot.Hosts).Distinct(StringComparer.OrdinalIgnoreCase)]; }
        catch (Exception error) when (error is JsonException or FlowFailed)
        {
            OsConsoleLog.For<ApprovalsNeuron>().LogSwallowed(error, "reading build unit needs hosts");
            hosts = [];
        }
        return hosts.Length == 0 ? sentence : $"{sentence} Reads {string.Join(", ", hosts)}.";
    }
}
