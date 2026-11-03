using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record MigrationIsSingle(bool Recorded) : Rule("apps.migration-single")
{
    public override bool Broken => Recorded;
    public override string Message => "Another app migration is already recorded.";
}
