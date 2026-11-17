using System.Text.Json.Serialization;
using DigitalBrain.Confirmation;
using DigitalBrain.Rules;
using DigitalBrain.State;

namespace DigitalBrain.Operations;

/// <summary>Where an operation is in its lifecycle.</summary>
public enum OperationPhase
{
    /// <summary>Started and actively executing.</summary>
    Running = 0,

    /// <summary>Ended with a terminal result.</summary>
    Done = 1,

    /// <summary>Waiting for approval before proceeding.</summary>
    AwaitingApproval = 2,
}

/// <summary>
/// The persisted state of one operation neuron (runtime.operation/id).
/// </summary>
[ExportableState]
public sealed record OperationState(
    string Id,
    string Brain,
    string Origin,
    string Kind,
    string PlanJson,
    long? InlineLength,
    string? InlineHash,
    OperationPhase Phase,
    bool CancelRequested,
    double Fraction,
    OperationResult? Result,
    DateTimeOffset Started,
    DateTimeOffset Updated,
    string? Driver = null,
    string? DriverLifetime = null,
    string? TraceId = null,
    string? ApprovalPlanId = null,
    string? Guard = null,
    DateTimeOffset? Resumed = null)
{
    /// <summary>The largest inline payload kept in state so an approved operation can still run after a restart.</summary>
    public const int MaxPersistedPayloadBytes = 256 * 1024;

    /// <summary>The inline payload while the operation is not done, when it fits <see cref="MaxPersistedPayloadBytes"/>; null otherwise.</summary>
    public byte[]? Payload { get; init; }

    // After a restart the in-memory plan is gone; the kept payload is put back only when it is exactly the one that was planned.
    internal OperationPlan Restore(OperationPlan persisted)
    {
        if (Payload is not { Length: > 0 } payload || InlineHash is not { } expected) { return persisted; }
        var actual = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        return string.Equals(actual, expected, StringComparison.Ordinal) ? persisted.WithPayload(payload) : persisted;
    }

    /// <summary>Creates an empty, uninitialized operation state.</summary>
    public OperationState()
        : this(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null, null, OperationPhase.Running, false, 0d, null, default, default)
    {
    }

    /// <summary>True when this operation has an id.</summary>
    [JsonIgnore]
    public bool Exists => !string.IsNullOrEmpty(Id);

    /// <summary>The typed operation id key.</summary>
    [JsonIgnore]
    public OperationId Key => new(Id);

    /// <summary>Where the operation is, read from the stored fields.</summary>
    [JsonIgnore]
    public OperationStage Stage => (Exists, Phase, CancelRequested) switch
    {
        (false, _, _) => new OperationStages.NotStarted(),
        (_, OperationPhase.Done, _) => new OperationStages.Done(Result ?? new OperationResult.Failed("unknown", "The operation ended without a result.")),
        (_, OperationPhase.AwaitingApproval, _) => new OperationStages.AwaitingApproval(ApprovalPlanId ?? string.Empty),
        (_, _, true) => new OperationStages.Cancelling(),
        _ => new OperationStages.Running(ApprovalPlanId is not null),
    };

    /// <summary>Places a running operation that was never approved into awaiting approval of <paramref name="planId"/>.</summary>
    public Outcome<OperationState> AwaitApproval(string planId, string? guard, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        return Stage switch
        {
            OperationStages.Running { Approved: false } => new Transition<OperationState>(
                this with { Phase = OperationPhase.AwaitingApproval, ApprovalPlanId = planId, Guard = guard, Updated = now },
                [new OperationAwaitingApproval(Key, planId)]),
            OperationStages.Running or OperationStages.AwaitingApproval => new OperationRefusals.ApprovalAsked(),
            OperationStages.Cancelling => new OperationRefusals.CancelPending(),
            OperationStages.Done => new OperationRefusals.AlreadyDone(),
            OperationStages.NotStarted => new OperationRefusals.NotStarted(),
        };
    }

    /// <summary>Answers the pending approval: an approval runs the operation, anything else ends it as failed.</summary>
    public Outcome<OperationState> Answer(ConfirmationResult answer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (Stage.Value is not OperationStages.AwaitingApproval awaiting) { return new OperationRefusals.NotAwaitingApproval(); }
        if (!string.Equals(answer.PlanId, awaiting.PlanId, StringComparison.Ordinal)) { return new OperationRefusals.PlanMismatch(awaiting.PlanId, answer.PlanId); }

        if (answer.Outcome == ConfirmationOutcome.Approved)
        {
            return new Transition<OperationState>(
                this with { Phase = OperationPhase.Running, Resumed = now, Updated = now },
                [new OperationProgress(Key, 0d)]);
        }

        var (code, reason) = answer.Outcome switch
        {
            ConfirmationOutcome.Expired => ("expired", "The confirmation expired."),
            _ when answer.Reason == ConfirmationDenyReason.SelfApproval => ("declined", "Only a person can approve this."),
            _ => ("declined", "The confirmation was denied."),
        };

        return Complete(new OperationResult.Failed(code, reason), now);
    }

    /// <summary>Requests cancellation: a running handler is asked to stop; otherwise the operation ends as cancelled.</summary>
    public Outcome<OperationState> RequestCancel(bool handlerRuns, DateTimeOffset now) => Stage switch
    {
        OperationStages.Done => new OperationRefusals.AlreadyDone(),
        OperationStages.Cancelling => new OperationRefusals.CancelPending(),
        OperationStages.Running when handlerRuns => new Transition<OperationState>(this with { CancelRequested = true, Updated = now }),
        _ => Complete(new OperationResult.Cancelled(), now),
    };

    /// <summary>Ends the operation with <paramref name="result"/>.</summary>
    public Outcome<OperationState> Complete(OperationResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Stage switch
        {
            OperationStages.NotStarted => new OperationRefusals.NotStarted(),
            OperationStages.Done => new OperationRefusals.AlreadyDone(),
            _ => new Transition<OperationState>(
                this with
                {
                    Phase = OperationPhase.Done,
                    Result = result,
                    Payload = null,
                    Fraction = result is OperationResult.Succeeded ? 1d : Fraction,
                    Updated = now,
                },
                [new OperationDone(Key, result)]),
        };
    }

    /// <summary>Records progress of a running operation, clamped to [0, 1].</summary>
    public Outcome<OperationState> Progress(double fraction, DateTimeOffset now)
    {
        var clamped = Math.Clamp(fraction, 0d, 1d);
        return Stage switch
        {
            OperationStages.Running => new Transition<OperationState>(this with { Fraction = clamped, Updated = now }, [new OperationProgress(Key, clamped)]),
            OperationStages.Cancelling => new OperationRefusals.CancelPending(),
            OperationStages.AwaitingApproval => new OperationRefusals.AwaitingApproval(),
            OperationStages.Done => new OperationRefusals.AlreadyDone(),
            OperationStages.NotStarted => new OperationRefusals.NotStarted(),
        };
    }
}
