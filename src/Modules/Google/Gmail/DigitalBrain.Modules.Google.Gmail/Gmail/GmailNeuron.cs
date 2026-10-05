using DigitalBrain;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace DigitalBrain.Google.Gmail;

[GrainType("gmail")]
internal sealed class GmailNeuron(
    [PersistentState("gmail-vault", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<GmailState> store)
    : Neuron<GmailState>(store), IGmail, IGmailInbound
{
    public async Task AcceptWatchPush(GmailWatchPush push)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(push.EmailAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(push.HistoryId);
        if (!string.Equals(Snapshot.Email, push.EmailAddress, StringComparison.OrdinalIgnoreCase)) { return; }
        var key = this.GetPrimaryKeyString();
        if (!key.StartsWith("connection/", StringComparison.Ordinal)) { return; }
        if (CallerContextStamper.TryGet(out _))
        {
            if (!await ServiceProvider.GetRequiredService<IModuleConnectionCredentials<GmailModule>>().IsConnectedAsync(key)) { return; }
        }
        else
        {
            // Verified Pub/Sub delivery enters this internal contract without a user stamp.
            // Keep the cluster's unstamped read path; never fabricate a user or Platform caller.
            var (brain, connector, connection) = ConnectionKeys.Parse(key);
            if (!(await GrainFactory.GetGrain<IConnectionRegistry>(brain).List())
                .Any(c => c.Id == connection && c.IntegrationId == connector && c.Status == ConnectionStatus.Connected)) { return; }
        }
        await PublishAsync(new MailReceived(push.EmailAddress, push.HistoryId));
    }

    public async Task AcceptAuthorizationCode(string authorizationCode, string? targetBrainId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        _ = ConnectionKeys.Parse(this.GetPrimaryKeyString());
        var tokens = ServiceProvider.GetRequiredService<IGmailTokenExchange>();
        var grant = await tokens.ExchangeAuthorizationCodeAsync(authorizationCode, CancellationToken.None)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        GmailTokenPolicy.ValidateToken(grant.AccessToken);
        if (grant.RefreshToken is not null)
        {
            GmailTokenPolicy.ValidateToken(grant.RefreshToken);
        }

        var email = grant.Email;
        if (string.IsNullOrWhiteSpace(email)) { throw new GmailUnavailableException("Google did not verify the mailbox identity. Reconnect Gmail."); }
        var key = this.GetPrimaryKeyString();
        if (!key.StartsWith("connection/", StringComparison.Ordinal)) { throw new GmailNotConnectedException("Reconnect Gmail as a named connection."); }
        var credentials = ServiceProvider.GetRequiredService<IModuleConnectionCredentials<GmailModule>>();
        var credential = await credentials.WriteSecretAsync(key, "gmail.access", "Gmail access token", grant.AccessToken, targetBrainId: targetBrainId);
        var refreshCredential = grant.RefreshToken is null ? null
            : await credentials.WriteSecretAsync(key, "gmail.refresh", "Gmail refresh token", grant.RefreshToken, targetBrainId: targetBrainId);
        if (Snapshot.Email is { } previous && !string.Equals(previous, email, StringComparison.OrdinalIgnoreCase))
        { await GrainFactory.GetGrain<IGmailMailboxIndex>(previous.ToLowerInvariant()).Remove(key); }
        var expiresAt = GmailTokenPolicy.Expiry(grant.ExpiresInSeconds, ServiceProvider.GetRequiredService<TimeProvider>());
        await credentials.ConnectAsync(key, "google-oauth", credential, targetBrainId: targetBrainId);
        await Save(Snapshot with
        {
            Email = email,
            Credential = credential,
            RefreshCredential = refreshCredential,
            GrantedScopes = grant.GrantedScopes ?? "",
            ExpiresAt = expiresAt,
        }, new GmailConnected(email)).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        await GrainFactory.GetGrain<IGmailMailboxIndex>(email.ToLowerInvariant()).Add(key);
    }

}
