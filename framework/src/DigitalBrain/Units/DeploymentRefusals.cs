using DigitalBrain.Rules;

namespace DigitalBrain.Units;

/// <summary>Why a unit deployment refused a move or a deploy. Each carries a stable code.</summary>
public static class DeploymentRefusals
{
    /// <summary>No build is staged.</summary>
    public sealed record NothingStaged() : Refusal("deploy.nothing-staged")
    {
        /// <inheritdoc />
        public override string Message => "No new version is waiting.";
    }

    /// <summary>The staged build does not wait for <paramref name="Alias"/>; for another unit's install this is ordinary traffic.</summary>
    public sealed record NotWaiting(string Alias) : Refusal("deploy.not-waiting")
    {
        /// <inheritdoc />
        public override string Message => $"The new version is not waiting for '{Alias}'.";
    }

    /// <summary>The person approved needs of <paramref name="Alias"/> that differ from what the staged build declares.</summary>
    public sealed record NeedsChanged(string Alias, string Staged, string Approved) : Refusal("deploy.needs-changed")
    {
        /// <inheritdoc />
        public override string Message => $"The approval for '{Alias}' was for other needs than the new version declares; it must be asked again.";
    }

    /// <summary>A denial names another plan than the one asked for <paramref name="Alias"/>.</summary>
    public sealed record PlanMismatch(string Alias, string Asked, string Answered) : Refusal("deploy.plan-mismatch")
    {
        /// <inheritdoc />
        public override string Message => $"The answer for '{Alias}' is for plan {Answered}, but the deployment asked about plan {Asked}.";
    }

    /// <summary><paramref name="Alias"/> is already owned by another source.</summary>
    public sealed record AliasOwned(string Alias, string Owner) : Refusal("deploy.alias-owned")
    {
        /// <inheritdoc />
        public override string Message => $"Alias '{Alias}' is already owned by source '{Owner}'.";
    }

    /// <summary>Installing <paramref name="Alias"/> was refused or denied.</summary>
    public sealed record InstallRefused(string Alias, string? Reason) : Refusal("deploy.install-refused")
    {
        /// <inheritdoc />
        public override string Message => Reason ?? $"Install of '{Alias}' refused.";
    }

    /// <summary>The bindings given for <paramref name="Alias"/> are refused.</summary>
    public sealed record BindingsRefused(string Alias, string Problem) : Refusal("deploy.bindings-refused")
    {
        /// <inheritdoc />
        public override string Message => $"the bindings of '{Alias}' are refused: {Problem}";
    }

    /// <summary>The proposal was superseded while it was being installed.</summary>
    public sealed record Superseded() : Refusal("deploy.superseded")
    {
        /// <inheritdoc />
        public override string Message => "The new version was replaced before it went live.";
    }
}
