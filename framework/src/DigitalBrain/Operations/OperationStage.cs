using DigitalBrain.Rules;

namespace DigitalBrain.Operations;

/// <summary>The stages of an operation, read from its stored fields. Each stage carries only what holds in it.</summary>
public static class OperationStages
{
    /// <summary>No operation has begun under this id.</summary>
    public sealed record NotStarted;

    /// <summary>Executing. <paramref name="Approved"/> is true once an approval was given; an operation asks at most once.</summary>
    public sealed record Running(bool Approved);

    /// <summary>Executing, with a cancel requested from the running handler.</summary>
    public sealed record Cancelling;

    /// <summary>Waiting for the person to answer plan <paramref name="PlanId"/>.</summary>
    public sealed record AwaitingApproval(string PlanId);

    /// <summary>Ended with <paramref name="Result"/>.</summary>
    public sealed record Done(OperationResult Result);
}

/// <summary>Where an operation is: one of <see cref="OperationStages"/>.</summary>
public union OperationStage(
    OperationStages.NotStarted,
    OperationStages.Running,
    OperationStages.Cancelling,
    OperationStages.AwaitingApproval,
    OperationStages.Done);

/// <summary>Why an operation refused a move. Each carries a stable code.</summary>
public static class OperationRefusals
{
    /// <summary>The operation has not begun.</summary>
    public sealed record NotStarted() : Refusal("operation.not-started")
    {
        /// <inheritdoc />
        public override string Message => "The operation has not begun.";
    }

    /// <summary>The operation has already ended.</summary>
    public sealed record AlreadyDone() : Refusal("operation.already-done")
    {
        /// <inheritdoc />
        public override string Message => "The operation has already ended.";
    }

    /// <summary>A cancel was already requested.</summary>
    public sealed record CancelPending() : Refusal("operation.cancel-pending")
    {
        /// <inheritdoc />
        public override string Message => "The operation is being cancelled.";
    }

    /// <summary>The operation waits for an approval, or was already approved once.</summary>
    public sealed record ApprovalAsked() : Refusal("operation.approval-asked")
    {
        /// <inheritdoc />
        public override string Message => "The operation already asked for approval.";
    }

    /// <summary>The operation is waiting for approval and cannot move until it is answered.</summary>
    public sealed record AwaitingApproval() : Refusal("operation.awaiting-approval")
    {
        /// <inheritdoc />
        public override string Message => "The operation is waiting for approval.";
    }

    /// <summary>An answer arrived while the operation was not waiting for one.</summary>
    public sealed record NotAwaitingApproval() : Refusal("operation.not-awaiting-approval")
    {
        /// <inheritdoc />
        public override string Message => "The operation is not waiting for approval.";
    }

    /// <summary>An answer names another plan than the one asked.</summary>
    /// <param name="Asked">The plan the operation asked about.</param>
    /// <param name="Answered">The plan the answer names.</param>
    public sealed record PlanMismatch(string Asked, string Answered) : Refusal("operation.plan-mismatch")
    {
        /// <inheritdoc />
        public override string Message => $"The answer is for plan {Answered}, but the operation asked about plan {Asked}.";
    }
}
