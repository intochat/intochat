using DigitalBrain.Confirmation;
using DigitalBrain.Contracts;
using DigitalBrain.Spi;
using IntoChat.OS.Confirmation;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Composition.Confirmations;

[NeuronAlias("runtime.confirmations")]
internal interface IConfirmationEntity
{
    Task Begin(ConfirmationPlan plan, string origin);
    Task<ConfirmationResult?> Result(string planId);
    Task ResolveAsRequester(string planId, bool approve);
    Task Cancel(string planId);
}

internal sealed record ConfirmationEntry(ConfirmationRequested Request, ConfirmationResult? Result = null);

internal sealed record ConfirmationState
{
    public long Revision { get; init; }
    public ConfirmationEntry[] Entries { get; init; } = [];
}

[DigitalBrain.Neuron("runtime.confirmations")]
internal sealed class ConfirmationNeuron(IOptions<ConfirmationOptions> options)
    : DigitalBrain.Neuron<ConfirmationState>, IConfirmationEntity, IConfirmationInbox, IRemindedNeuron
{
    private const string ExpiryTimer = "confirmation-expiry";

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        if (Id.Key != "confirmations") { throw new InvalidOperationException("A confirmation inbox has the key confirmations."); }
        await Expire();
        Schedule();
    }

    public async Task Begin(ConfirmationPlan plan, string origin)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        if (plan.Id.Length > 128 || plan.Expiry <= TimeSpan.Zero) { throw new ArgumentException("Invalid confirmation plan.", nameof(plan)); }
        RequireOwnCaller(required: false);
        if (State.Entries.Any(entry => entry.Request.PlanId == plan.Id))
        { throw new InvalidOperationException("This confirmation plan was already requested."); }
        var requester = Requester(origin);
        var policy = options.Value.For(Id.Brain.Value);
        var expiry = plan.Expiry < ConfirmationRules.DefaultExpiry ? plan.Expiry : ConfirmationRules.DefaultExpiry;
        var request = new ConfirmationRequested(plan.Id, plan with { Expiry = expiry }, requester, policy, Context.Time.GetUtcNow() + expiry);
        var asks = ConfirmationRules.Asks(policy, plan);
        var result = asks ? null : new ConfirmationResult(plan.Id, ConfirmationOutcome.Approved, ConfirmationDenyReason.None, true);
        var next = State with { Revision = State.Revision + 1, Entries = [.. State.Entries, new(request, result)] };
        await Save(next, asks ? request : Resolved(request, result!, null));
        Schedule();
    }

    public async Task<ConfirmationInbox> Read()
    {
        RequireOwnCaller(required: true);
        await Expire();
        return new(State.Revision, [.. State.Entries.Where(entry => entry.Result is null).Select(entry => entry.Request)]);
    }

    public async Task<ConfirmationResult?> Result(string planId)
    {
        RequireOwnCaller(required: false);
        await Expire();
        return State.Entries.FirstOrDefault(entry => entry.Request.PlanId == planId)?.Result;
    }

    public async Task Resolve(string planId, bool approve)
    {
        var caller = RequireOwnCaller(required: true)!;
        // A human stamp inherited through a neuron's Get is a relayed click, not a direct edge answer.
        var direct = string.IsNullOrEmpty(global::Orleans.Runtime.RequestContext.Get(KernelInvocationSink.CallerKey) as string);
        var human = direct && caller.Kind == CallerKind.User && caller.StampedBy == TrustedEdge.AuthenticatedHttp
            && caller.AppId is null && caller.OnBehalfOf is null && !AppPrincipal.IsApp(caller.PrincipalId)
            && !CallerContextStamper.IsModelOriginated(caller);
        var principal = human ? ConfirmationPrincipal.Person(caller.PrincipalId) : ConfirmationPrincipal.Agent(caller.PrincipalId);
        await Expire();
        await End(planId, !approve ? ConfirmationOutcome.Denied : human ? ConfirmationOutcome.Approved : ConfirmationOutcome.Denied,
            !approve ? ConfirmationDenyReason.ByPerson : human ? ConfirmationDenyReason.None : ConfirmationDenyReason.SelfApproval, principal);
    }

    public async Task ResolveAsRequester(string planId, bool approve)
    {
        RequireOwnCaller(required: false);
        await Expire();
        await End(planId, ConfirmationOutcome.Denied,
            approve ? ConfirmationDenyReason.SelfApproval : ConfirmationDenyReason.ByPerson, Requester("requester"));
    }

    public Task Cancel(string planId)
    {
        RequireOwnCaller(required: false);
        return End(planId, ConfirmationOutcome.Denied, ConfirmationDenyReason.PlanInvalid, null);
    }

    public Task OnReminder(string name, CancellationToken cancellationToken) => name == ExpiryTimer ? Expire() : Task.CompletedTask;

    private CallerContext? RequireOwnCaller(bool required)
    {
        if (!CallerContextStamper.TryGet(out var caller))
        {
            if (required) { throw new UnauthorizedAccessException("A trusted caller is required."); }
            return null;
        }
        if (!CallerContextStamper.IsTrusted(caller) || caller.BrainId != Id.Brain.Value)
        { throw new UnauthorizedAccessException("This confirmation belongs to another brain."); }
        return caller;
    }

    private static ConfirmationPrincipal Requester(string origin)
    {
        if (!CallerContextStamper.TryGet(out var caller)) { return ConfirmationPrincipal.App(origin); }
        return caller.Kind switch
        {
            CallerKind.User when !CallerContextStamper.IsModelOriginated(caller) => ConfirmationPrincipal.Person(caller.PrincipalId),
            CallerKind.App => ConfirmationPrincipal.App(caller.PrincipalId),
            _ => ConfirmationPrincipal.Agent(caller.PrincipalId),
        };
    }

    private async Task Expire()
    {
        var now = Context.Time.GetUtcNow();
        foreach (var entry in State.Entries.Where(entry => entry.Result is null && entry.Request.ExpiresAt <= now).ToArray())
        { await End(entry.Request.PlanId, ConfirmationOutcome.Expired, ConfirmationDenyReason.Expired, null); }
    }

    private async Task End(string planId, ConfirmationOutcome outcome, ConfirmationDenyReason reason, ConfirmationPrincipal? approver)
    {
        var index = Array.FindIndex(State.Entries, entry => entry.Request.PlanId == planId);
        if (index < 0 || State.Entries[index].Result is not null) { return; }
        var entry = State.Entries[index];
        var result = new ConfirmationResult(planId, outcome, reason, false);
        var entries = State.Entries.ToArray();
        entries[index] = entry with { Result = result };
        await Save(State with { Revision = State.Revision + 1, Entries = entries }, Resolved(entry.Request, result, approver));
        Schedule();
    }

    private ConfirmationResolved Resolved(ConfirmationRequested request, ConfirmationResult result, ConfirmationPrincipal? approver)
        => new(request.PlanId, request.Plan.Action, request.Plan.TargetLabel, request.Requester, approver, request.Policy,
            result.Outcome, result.Reason, result.Skipped, Context.Time.GetUtcNow());

    private void Schedule()
    {
        var pending = State.Entries.Where(entry => entry.Result is null).ToArray();
        if (pending.Length == 0) { Context.Timers.Stop(ExpiryTimer); return; }
        var due = pending.Min(entry => entry.Request.ExpiresAt) - Context.Time.GetUtcNow();
        Context.Timers.Start(ExpiryTimer, due > TimeSpan.Zero ? due : TimeSpan.FromMilliseconds(1), null, keepAlive: true);
    }
}

[DigitalBrain.Module("kernel-confirmations")]
public sealed class ConfirmationNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}
