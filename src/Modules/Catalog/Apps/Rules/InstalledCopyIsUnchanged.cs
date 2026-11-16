using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record InstalledCopyIsUnchanged(AppBundle Held, AppInstallReceipt Installation) : Rule("apps.installed-copy")
{
    public override bool Broken => Held.Version != Installation.Publication.Version || AppPublicationDigest.Of(Held) != Installation.Publication.Digest;
    public override string Message => "This installed copy has changed; it cannot resume the original install.";
}
