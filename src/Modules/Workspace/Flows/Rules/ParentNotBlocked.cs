using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows.Rules;

// A flow is not started under a parent that another open flow blocks.
internal sealed record ParentNotBlocked(string? Blocker) : Rule("apps.flows-parent-not-blocked")
{
    public override bool Broken => Blocker is not null;
    public override string Message => $"blocked by {Blocker}";
}
