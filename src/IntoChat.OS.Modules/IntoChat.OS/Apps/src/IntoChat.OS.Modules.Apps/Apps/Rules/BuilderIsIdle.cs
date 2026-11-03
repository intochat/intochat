using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record BuilderIsIdle(bool Building) : Rule("apps.builder-idle")
{
    public override bool Broken => Building;
    public override string Message => "The Builder is already working on this app.";
}
