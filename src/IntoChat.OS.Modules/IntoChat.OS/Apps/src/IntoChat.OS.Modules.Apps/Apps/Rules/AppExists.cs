using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record AppExists(string AppName, bool Exists) : Rule("apps.exists")
{
    public override bool Broken => !Exists;
    public override string Message => $"No app named {AppName} exists in this brain.";
}
