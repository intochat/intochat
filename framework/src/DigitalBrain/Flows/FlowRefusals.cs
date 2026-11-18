using DigitalBrain.Rules;

namespace DigitalBrain.Flows;

/// <summary>Why a flow refused a move. Each carries a stable code.</summary>
public static class FlowRefusals
{
    /// <summary>The flow has already started.</summary>
    public sealed record AlreadyStarted() : Refusal("flow.already-started")
    {
        /// <inheritdoc />
        public override string Message => "The flow has already started.";
    }

    /// <summary>The flow has completed, failed or been cancelled.</summary>
    public sealed record AlreadyOver() : Refusal("flow.already-over")
    {
        /// <inheritdoc />
        public override string Message => "The flow has already ended.";
    }

    /// <summary>The flow is not running a step right now: it waits, or has ended.</summary>
    public sealed record NotAdvancing() : Refusal("flow.not-advancing")
    {
        /// <inheritdoc />
        public override string Message => "The flow is not running a step.";
    }

    /// <summary><paramref name="Step"/> is not the step the flow is running.</summary>
    public sealed record StepNotRunning(string Step) : Refusal("flow.step-not-running")
    {
        /// <inheritdoc />
        public override string Message => $"The flow is not running step '{Step}'.";
    }

    /// <summary>The flow does not wait on <paramref name="Step"/>.</summary>
    public sealed record NotWaitingOn(string Step) : Refusal("flow.not-waiting")
    {
        /// <inheritdoc />
        public override string Message => $"The flow is not waiting on step '{Step}'.";
    }

    /// <summary>The flow already holds this relation.</summary>
    public sealed record AlreadyRelated() : Refusal("flow.already-related")
    {
        /// <inheritdoc />
        public override string Message => "The flow already holds this relation.";
    }
}
