using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record ReceiptMatchesPublication(bool Matches) : Rule("apps.receipt")
{
    public override bool Broken => !Matches;
    public override string Message => "The installation receipt does not match this publication.";
}
