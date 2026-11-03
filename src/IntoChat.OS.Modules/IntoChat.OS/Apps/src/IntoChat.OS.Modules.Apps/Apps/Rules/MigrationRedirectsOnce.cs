using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record MigrationRedirectsOnce(bool Redirected) : Rule("apps.migration-redirect")
{
    public override bool Broken => Redirected;
    public override string Message => "This migration already redirects elsewhere.";
}
