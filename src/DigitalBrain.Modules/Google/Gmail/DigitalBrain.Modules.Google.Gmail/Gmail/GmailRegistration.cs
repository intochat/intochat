using DigitalBrain.Framework.Platform.Integrations;
namespace DigitalBrain.Google.Gmail;

internal sealed record GmailOAuthCredentials(string ClientId, string ClientSecret, Uri PublicOrigin);
internal static class GmailScopes { internal const string Read = "https://www.googleapis.com/auth/gmail.readonly"; }
internal sealed class GmailRegistration(IModuleIntegrations<GmailModule> integrations, GmailModuleOptions options)
{
    internal Task<IntegrationSnapshot> ReadAsync() => integrations.Get("google-oauth").ReadAsync();
    internal Uri? PublicOrigin => options.PublicOrigin;
    internal GmailUnavailability? Explain(IntegrationSnapshot snapshot)
        => snapshot.Status != IntegrationStatus.Ready ? new("google-oauth", snapshot.Status.ToString(), snapshot.MissingFields)
            : PublicOrigin is null ? new("google-oauth", "Invalid", ["PublicOrigin"]) : null;
    internal async Task<GmailOAuthCredentials> ReleaseAsync()
    {
        if (PublicOrigin is null) { throw new GmailUnavailableException("Gmail is not configured for this deployment."); }
        var released = await integrations.Get("google-oauth").ReleaseAsync();
        if (Explain(released.Snapshot) is not null) { throw new GmailUnavailableException("Gmail is not configured for this deployment."); }
        return new(released.Values["ClientId"], released.Values["ClientSecret"], PublicOrigin);
    }
}
internal sealed record GmailUnavailability(string Integration, string Status, string[] Missing);
