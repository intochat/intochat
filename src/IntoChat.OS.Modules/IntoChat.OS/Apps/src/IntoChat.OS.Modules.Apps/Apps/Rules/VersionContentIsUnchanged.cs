using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record VersionContentIsUnchanged(bool SameContent) : Rule("apps.version-content")
{
    public override bool Broken => !SameContent;
    public override string Message => "This version has already been published with different content. Create a new version.";
}
