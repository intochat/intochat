using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record AppNameIsFree(string AppName, bool Exists) : Rule("apps.name-free")
{
    public override bool Broken => Exists;
    public override string Message => $"You already have an app named {AppName}. Open it in the Creator, or describe this one differently.";
}
