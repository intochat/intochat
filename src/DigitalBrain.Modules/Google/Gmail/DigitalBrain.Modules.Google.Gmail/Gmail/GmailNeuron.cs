using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Google.Gmail;

[GrainType("gmail")]
internal sealed class GmailNeuron(
    [PersistentState("gmail", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<GmailState> store)
    : ConnectionNeuron<GmailState>(store), IGmail, IGmailInbound
{
    // Verified Pub/Sub delivery arrives unstamped; the connection's own state decides. A stamped
    // caller from another brain is refused, so it cannot inject mail into this one.
    public async Task AcceptWatchPush(GmailWatchPush push)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(push.EmailAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(push.HistoryId);
        if (CallerContextStamper.TryGet(out var caller) && BrainScope.Create(caller.AccountId, caller.BrainId).Id != Key.Brain)
        { throw new UnauthorizedAccessException($"Only the owning brain can deliver to connection gmail/{Key.Connection}."); }
        // Expired or Failing still receive: a probe may have seen a lapsed access token that the
        // refresh below renews, and the refresh moves the status back to Connected.
        if (Snapshot.Status == ConnectionStatus.Disconnected || Snapshot.Secrets.Length == 0) { return; }
        if (!string.Equals(Snapshot.Account, push.EmailAddress, StringComparison.OrdinalIgnoreCase)) { return; }
        // Refreshing here keeps the watch alive across token expiry without a Gmail API call.
        try { await UseCredentialAsync((_, _) => Task.FromResult(true), CancellationToken.None); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // One revoked grant must not fail the fan-out to the brain's other Gmail connections.
            // The message may carry a provider reply; only the type is kept.
            await MarkExpired($"Google refused the Gmail token refresh ({error.GetType().Name}). Reconnect Gmail.");
            return;
        }
        await PublishAsync(new MailReceived(push.EmailAddress, push.HistoryId));
    }

    // The grant proves the mailbox: Google's profile endpoint names the address the token reads.
    protected override async Task<OAuthGrant> AcceptGrantAsync(OAuthGrant grant)
    {
        using var profile = await ProfileAsync(grant.AccessToken, CancellationToken.None);
        if (!profile.IsSuccessStatusCode) { throw new GmailUnavailableException("Google did not verify the mailbox identity. Reconnect Gmail."); }
        using var json = JsonDocument.Parse(await profile.Content.ReadAsStringAsync());
        var email = json.RootElement.TryGetProperty("emailAddress", out var address) && address.ValueKind == JsonValueKind.String ? address.GetString() : null;
        if (string.IsNullOrWhiteSpace(email)) { throw new GmailUnavailableException("Google did not verify the mailbox identity. Reconnect Gmail."); }
        Snapshot.Account = email;
        return grant;
    }

    // Google, not the clock, says whether the stored token still reads the mailbox.
    protected override async Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        using var profile = await ProfileAsync(credential.Value, ct);
        return profile.StatusCode switch
        {
            HttpStatusCode.OK => ConnectionProbeResult.Connected(),
            HttpStatusCode.Unauthorized => ConnectionProbeResult.Expired("The Gmail access token was refused."),
            var status => ConnectionProbeResult.Failing($"Google answered the profile check with HTTP {(int)status}."),
        };
    }

    private async Task<HttpResponseMessage> ProfileAsync(string accessToken, CancellationToken ct)
    {
        var options = ServiceProvider.GetRequiredService<GmailModuleOptions>();
        using var client = ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(GmailModule.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, options.ProfileEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, ct);
    }
}
