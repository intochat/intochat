using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record HasPublishedVersion(AppVersionRecord? Published) : Rule("apps.published-version")
{
    public override bool Broken => Published is null;
    public override string Message => "This app has no published version.";
}
