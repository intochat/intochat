using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record HasPublishedVersion(bool Any) : Rule("apps.published-version")
{
    public override bool Broken => !Any;
    public override string Message => "This app has no published version.";
}
