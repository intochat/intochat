using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record ReceiptMatchesPublication(AppInstallReceipt Installation, AppOrigin? Origin, AppBundle Bundle, string AppName, string AccountId) : Rule("apps.receipt")
{
    public override bool Broken => !PublishedRelease.IsReceiptMatching(Installation, Origin, Bundle, AppName, AccountId);
    public override string Message => "The installation receipt does not match this publication.";
}
