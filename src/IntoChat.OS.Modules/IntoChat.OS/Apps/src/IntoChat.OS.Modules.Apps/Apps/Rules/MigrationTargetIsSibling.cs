using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record MigrationTargetIsSibling(string Target, string Brain) : Rule("apps.migration-target")
{
    public override bool Broken => IApp.NameOf(Brain, Target) is null;
    public override string Message => "A migration target must be another app in the same brain.";
}
