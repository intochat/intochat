using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record AppExists(string AppName, AppManifest? Manifest) : Rule("apps.exists")
{
    public override bool Broken => Manifest is null;
    public override string Message => $"No app named {AppName} exists in this brain.";
}
