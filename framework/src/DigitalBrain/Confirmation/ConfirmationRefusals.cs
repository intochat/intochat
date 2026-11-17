using DigitalBrain.Rules;

namespace DigitalBrain.Confirmation;

/// <summary>Why a confirmation inbox refused a move. Each carries a stable code.</summary>
public static class ConfirmationRefusals
{
    /// <summary>The same raiser asked the same question again; the first answer stands.</summary>
    public sealed record AlreadyAsked(string PlanId) : Refusal("confirmation.already-asked")
    {
        /// <inheritdoc />
        public override string Message => "This question was already asked.";
    }

    /// <summary>Another raiser, or another question, already uses <paramref name="PlanId"/>.</summary>
    public sealed record AlreadyRequested(string PlanId) : Refusal("confirmation.already-requested")
    {
        /// <inheritdoc />
        public override string Message => "This confirmation plan was already requested.";
    }

    /// <summary>The raiser already has <see cref="ConfirmationState.PendingLimit"/> questions waiting.</summary>
    public sealed record TooManyWaiting() : Refusal("confirmation.too-many-waiting")
    {
        /// <inheritdoc />
        public override string Message => $"This raiser already has {ConfirmationState.PendingLimit} confirmations waiting.";
    }

    /// <summary>Only a person may answer this question.</summary>
    public sealed record PersonOnly(string PlanId) : Refusal("confirmation.person-only")
    {
        /// <inheritdoc />
        public override string Message => "Only a person answers this question.";
    }

    /// <summary>The question is unknown, or was already answered, withdrawn or expired.</summary>
    public sealed record NotOpen(string PlanId) : Refusal("confirmation.not-open")
    {
        /// <inheritdoc />
        public override string Message => "This question is no longer waiting for an answer.";
    }
}
