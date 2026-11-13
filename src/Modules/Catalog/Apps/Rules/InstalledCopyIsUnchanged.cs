using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record InstalledCopyIsUnchanged(bool Edited) : Rule("apps.installed-copy")
{
    public override bool Broken => Edited;
    public override string Message => "This installed copy has changed; it cannot resume the original install.";
}
