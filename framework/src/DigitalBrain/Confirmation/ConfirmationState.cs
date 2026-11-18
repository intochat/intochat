using System.Text.Json.Serialization;
using DigitalBrain.Rules;
using DigitalBrain.State;

namespace DigitalBrain.Confirmation;

/// <summary>
/// A confirmation entry tracked in state.
/// </summary>
/// <param name="Request">The requested confirmation plan details.</param>
/// <param name="Result">The resolution result, or null if pending human action.</param>
/// <param name="RaisedBy">The identity of the calling neuron/requester.</param>
public sealed record ConfirmationEntry(
    ConfirmationRequested Request,
    ConfirmationResult? Result = null,
    string? RaisedBy = null);

/// <summary>
/// The append-only confirmation audit. One <see cref="ConfirmationResolved"/> per outcome, journaled with the
/// state change. The record names the target by <see cref="ConfirmationPlan.TargetLabel"/> only.
/// </summary>
public static class ConfirmationAudit
{
    public static ConfirmationResolved Record(ConfirmationRequested request, ConfirmationResult result, ConfirmationPrincipal? approver, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        return new(request.PlanId, request.Plan.Action, request.Plan.TargetLabel, request.Requester, approver, request.Policy,
            result.Outcome, result.Reason, result.Skipped, at);
    }
}

/// <summary>
/// Pure state of the confirmations neuron. Enforces the four core invariants:
/// 1. Maximum of 20 pending confirmations per raiser.
/// 2. Plan id length up to 128 characters and positive expiry.
/// 3. Expiry capped at DefaultExpiry (5 minutes), or up to 7 days for person-required plans.
/// 4. Idempotent re-raise by same caller/neuron and matching plan shape.
/// </summary>
[ExportableState]
public sealed record ConfirmationState
{
    public const int PendingLimit = 20;
    public const int MaxEntries = 50;
    public const int MaxIdLength = 128;
    internal static readonly TimeSpan MaxPersonExpiry = TimeSpan.FromDays(7);

    public long Revision { get; init; }
    public ConfirmationEntry[] Entries { get; init; } = [];

    public ConfirmationResult? Known(string planId)
        => Entries.FirstOrDefault(entry => entry.Request.PlanId == planId)?.Result;

    public Outcome<ConfirmationState> Request(
        ConfirmationPlan plan,
        ConfirmationPrincipal requester,
        string raisedBy,
        ConfirmationPolicy policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Id);

        if (plan.Id.Length > MaxIdLength || plan.Expiry <= TimeSpan.Zero)
        {
            throw new ArgumentException("Invalid confirmation plan.", nameof(plan));
        }

        if (Entries.FirstOrDefault(entry => entry.Request.PlanId == plan.Id) is { } known)
        {
            var sameCaller = SameRaiser(known, requester, raisedBy);
            var sameShape = known.Request.Plan.RequiresPerson == plan.RequiresPerson
                && string.Equals(known.Request.Plan.Kind, plan.Kind, StringComparison.Ordinal)
                && string.Equals(known.Request.Plan.Subject, plan.Subject, StringComparison.Ordinal);
            return sameCaller && sameShape ? new ConfirmationRefusals.AlreadyAsked(plan.Id) : new ConfirmationRefusals.AlreadyRequested(plan.Id);
        }

        if (Entries.Count(entry => entry.Result is null && SameRaiser(entry, requester, raisedBy)) >= PendingLimit)
        {
            return new ConfirmationRefusals.TooManyWaiting();
        }

        var maxExpiry = plan.RequiresPerson ? MaxPersonExpiry : ConfirmationRules.DefaultExpiry;
        var expiry = plan.Expiry < maxExpiry ? plan.Expiry : maxExpiry;
        var request = new ConfirmationRequested(plan.Id, plan with { Expiry = expiry }, requester, policy, now + expiry);

        var asks = ConfirmationRules.Asks(policy, plan);
        var result = asks ? null : new ConfirmationResult(plan.Id, ConfirmationOutcome.Approved, ConfirmationDenyReason.None, true);

        var next = this with
        {
            Revision = Revision + 1,
            Entries = Bound([.. Entries, new ConfirmationEntry(request, result, raisedBy)])
        };

        var signal = asks ? (Signal)request : ConfirmationAudit.Record(request, result!, null, now);
        return new Transition<ConfirmationState>(next, [signal]);
    }

    public Outcome<ConfirmationState> Answer(
        string planId,
        bool approve,
        Answerer answerer,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(answerer);

        if (Entries.FirstOrDefault(entry => entry.Request.PlanId == planId && entry.Result is null) is { } open
            && open.Request.Plan.RequiresPerson
            && !answerer.IsPerson)
        {
            return new ConfirmationRefusals.PersonOnly(planId);
        }

        var outcome = approve && answerer.IsPerson ? ConfirmationOutcome.Approved : ConfirmationOutcome.Denied;
        var reason = answerer.IsPerson
            ? (approve ? ConfirmationDenyReason.None : ConfirmationDenyReason.ByPerson)
            : ConfirmationDenyReason.SelfApproval;

        return End(planId, outcome, reason, answerer.Principal, now);
    }

    private static bool SameRaiser(ConfirmationEntry entry, ConfirmationPrincipal requester, string raisedBy)
        => entry.RaisedBy is { } by ? string.Equals(by, raisedBy, StringComparison.Ordinal) : entry.Request.Requester == requester;

    public Outcome<ConfirmationState> Withdraw(string planId, DateTimeOffset now)
        => End(planId, ConfirmationOutcome.Denied, ConfirmationDenyReason.PlanInvalid, null, now);

    public Outcome<ConfirmationState> Expire(string planId, DateTimeOffset now)
        => End(planId, ConfirmationOutcome.Expired, ConfirmationDenyReason.Expired, null, now);

    public Outcome<ConfirmationState> End(
        string planId,
        ConfirmationOutcome outcome,
        ConfirmationDenyReason reason,
        ConfirmationPrincipal? approver,
        DateTimeOffset now)
    {
        var index = Array.FindIndex(Entries, entry => entry.Request.PlanId == planId);
        if (index < 0 || Entries[index].Result is not null)
        {
            return new ConfirmationRefusals.NotOpen(planId);
        }

        var entry = Entries[index];
        var result = new ConfirmationResult(planId, outcome, reason, false) { Approver = approver };
        var entries = Entries.ToArray();
        entries[index] = entry with { Result = result };

        var next = this with
        {
            Revision = Revision + 1,
            Entries = Bound(entries)
        };

        return new Transition<ConfirmationState>(next, [ConfirmationAudit.Record(entry.Request, result, approver, now)]);
    }

    public static ConfirmationEntry[] Bound(ConfirmationEntry[] entries)
    {
        if (entries.Length <= MaxEntries) { return entries; }
        var pending = entries.Where(e => e.Result is null).ToList();
        var allowedCompleted = Math.Max(0, MaxEntries - pending.Count);
        var answered = entries.Where(e => e.Result is { Skipped: false }).ToList();
        var unasked = entries.Where(e => e.Result is { Skipped: true }).ToList();
        var keptAnswered = answered.Count > allowedCompleted ? answered.TakeLast(allowedCompleted).ToList() : answered;
        var remainingUnasked = Math.Max(0, allowedCompleted - keptAnswered.Count);
        var keptUnasked = unasked.Count > remainingUnasked ? unasked.TakeLast(remainingUnasked) : unasked;
        var keptPlanIds = new HashSet<string>(
            keptAnswered.Concat(keptUnasked).Select(e => e.Request.PlanId),
            StringComparer.Ordinal);
        return entries.Where(e => e.Result is null || keptPlanIds.Contains(e.Request.PlanId)).ToArray();
    }
}
