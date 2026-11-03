using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record ProgramIsNotPaused(bool Paused) : Rule("apps.program-paused")
{
    public override bool Broken => Paused;
    public override string Message => "The app program is paused for migration.";
}
