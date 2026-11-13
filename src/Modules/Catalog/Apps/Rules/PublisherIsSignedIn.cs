using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record PublisherIsSignedIn(string? PersonId) : Rule("apps.publisher-signed-in")
{
    public override bool Broken => string.IsNullOrWhiteSpace(PersonId);
    public override string Message => "Publishing needs a signed-in publisher.";
}
