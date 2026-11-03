using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record PublicationIsNotForeign(string? PublishedFrom, string Brain) : Rule("apps.publication-brain")
{
    public override bool Broken => PublishedFrom is not null && PublishedFrom != Brain;
    public override string Message => $"{PublishedFrom} is already published from another brain.";
}
