using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record MigrationIsPausedFirst(bool Paused) : Rule("apps.migration-paused-first")
{
    public override bool Broken => !Paused;
    public override string Message => "Pause this app under the same migration first.";
}
