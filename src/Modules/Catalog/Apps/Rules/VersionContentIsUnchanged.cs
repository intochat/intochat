using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record VersionContentIsUnchanged(AppReleaseRecord? Existing, AppPublicationRef ExpectedReference) : Rule("apps.version-content")
{
    public override bool Broken => Existing is not null && Existing.Publication.Reference != ExpectedReference;
    public override string Message => "This version has already been published with different content. Create a new version.";
}
