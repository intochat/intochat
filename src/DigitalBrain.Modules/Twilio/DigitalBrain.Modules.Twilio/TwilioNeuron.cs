using System.Text.RegularExpressions;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Twilio.Signals;

namespace DigitalBrain.Twilio;

// The connection's Account is its AccountSid. Recent messages dedup by ClientReference; Reported
// remembers which (sid, status) callbacks were already published, since Twilio retries deliveries.
[GenerateSerializer, Alias("twilio.state")]
internal sealed class TwilioState : ConnectionState
{
    [Id(8)] public SmsMessage[] Messages { get; set; } = [];
    [Id(9)] public string[] Reported { get; set; } = [];
}

// A "credentials" connection sends on the account sealed in its own state; a "deployment" connection
// sends on the deployment's Twilio integration (its sealed value is a placeholder and is ignored).
[GrainType("twilio")]
internal sealed partial class TwilioNeuron(
    ITwilioProvider provider, TimeProvider time, TwilioCallbackOrigin origin, IModuleIntegrations<TwilioModule> integrations,
    [PersistentState("twilio", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<TwilioState> store)
    : ConnectionNeuron<TwilioState>(store), ITwilioSms, ITwilioInbound
{
    public const int MaxBody = 1600;
    internal const string DeploymentMethod = "deployment";
    private const int MaxMessages = 100;
    private const int MaxReported = 400;
    private const string NotConfigured = "Twilio is not configured";

    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164();

    public async Task<SmsMessage> Send(SendSms command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.To is null || !E164().IsMatch(command.To))
        { throw new ArgumentException("To must be an E.164 number such as +51987654321.", nameof(command)); }
        if (string.IsNullOrWhiteSpace(command.Body) || command.Body.Length > MaxBody)
        { throw new ArgumentException($"Body must be 1 to {MaxBody} characters.", nameof(command)); }
        if (command.ClientReference is { Length: > 200 })
        { throw new ArgumentException("ClientReference must be at most 200 characters.", nameof(command)); }
        RequireOwnBrain();

        if (command.ClientReference is { } reference && Snapshot.Messages.FirstOrDefault(m => m.ClientReference == reference) is { } earlier)
        { return earlier; }

        var account = await UseCredentialAsync(AccountAsync, CancellationToken.None);
        if (account is null)
        {
            await Save(Snapshot, new SmsFailed(command.To, NotConfigured));
            throw new TwilioUnavailableException($"{NotConfigured} for this connection.");
        }

        var result = await provider.SendAsync(account, command.To, command.Body, origin.StatusCallback(Key.Brain), CancellationToken.None);
        var now = time.GetUtcNow();
        switch (result.Outcome)
        {
            case TwilioOutcome.Accepted:
                var message = new SmsMessage(result.Sid!, command.To, result.Status, now, command.ClientReference, result.ErrorCode);
                Snapshot.Messages = [.. Snapshot.Messages.Append(message).TakeLast(MaxMessages)];
                await Save(Snapshot, new SmsSent(message));
                return message;
            case TwilioOutcome.Unavailable:
                await Save(Snapshot, new SmsFailed(command.To, result.Detail));
                throw new TwilioUnavailableException($"Twilio did not take the message: {result.Detail}.");
            default:
                // A refusal is not kept for dedup, so a retry with the same reference sends once fixed.
                await Save(Snapshot, new SmsFailed(command.To, result.Detail));
                return new SmsMessage("", command.To, "failed", now, command.ClientReference, result.ErrorCode ?? (result.Outcome == TwilioOutcome.Unauthorized ? "401" : null));
        }
    }

    public Task<SmsMessage[]> ReadRecent(int limit)
    {
        if (limit is < 1 or > MaxMessages) { throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be between 1 and {MaxMessages}."); }
        RequireOwnBrain();
        return Task.FromResult(Snapshot.Messages.Reverse().Take(limit).ToArray());
    }

    // Twilio's delivery arrives unstamped and proves itself by its signature; a stamped caller from
    // another brain is refused, so it cannot report statuses into this one.
    public async Task<bool> AcceptStatus(TwilioStatusCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (CallerContextStamper.TryGet(out var caller) && BrainScope.Create(caller.AccountId, caller.BrainId).Id != Key.Brain)
        { throw new UnauthorizedAccessException($"Only the owning brain can deliver to connection twilio/{Key.Connection}."); }
        if (Snapshot.Account is null || Snapshot.Account != callback.Field("AccountSid")) { return false; }

        TwilioAccount? account;
        try { account = await UseCredentialAsync(AccountAsync, CancellationToken.None); }
        catch (InvalidOperationException) { return false; }
        if (account is null || account.AccountSid != Snapshot.Account) { return false; }
        if (!TwilioSignature.Matches(account.AuthToken, callback.Url, callback.Fields, callback.Signature)) { return false; }

        var sid = callback.Field("MessageSid");
        var status = callback.Field("MessageStatus") ?? callback.Field("SmsStatus");
        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(status)) { return true; }
        var reported = $"{sid}:{status}";
        if (Snapshot.Reported.Contains(reported)) { return true; }

        var code = callback.Field("ErrorCode");
        var known = Snapshot.Messages.FirstOrDefault(m => m.Sid == sid);
        if (known is not null)
        {
            Snapshot.Messages = [.. Snapshot.Messages.Select(m => m.Sid == sid ? m with { Status = status, ErrorCode = string.IsNullOrEmpty(code) ? m.ErrorCode : code } : m)];
        }
        Snapshot.Reported = [.. Snapshot.Reported.Append(reported).TakeLast(MaxReported)];
        Signal signal = status is "failed" or "undelivered"
            ? new SmsFailed(known?.To ?? callback.Field("To") ?? "", string.IsNullOrEmpty(code) ? status : $"{status} (Twilio error {code})")
            : new SmsDelivered(sid, status);
        await Save(Snapshot, signal);
        return true;
    }

    protected override async Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        var account = await AccountAsync(credential, ct);
        if (account is null) { return ConnectionProbeResult.Failing(Unusable()); }
        return await provider.ProbeAsync(account, ct) switch
        {
            TwilioOutcome.Accepted => ConnectionProbeResult.Connected(),
            TwilioOutcome.Unauthorized => ConnectionProbeResult.Failing("Twilio refused the credentials: unauthorized (401)."),
            _ => ConnectionProbeResult.Failing("Twilio did not answer the account probe."),
        };
    }

    // The account names the connection and gates which status callbacks it accepts.
    protected override async Task OnConnectedAsync()
    {
        var account = await UseCredentialAsync(AccountAsync, CancellationToken.None);
        if (account is null)
        {
            await MarkFailing(Unusable());
            return;
        }
        Snapshot.Account = account.AccountSid;
        await SaveQuietly(Snapshot);
    }

    private string Unusable() => Snapshot.AuthMethodId == DeploymentMethod
        ? $"{NotConfigured} on this deployment."
        : $"The pasted credentials must read {TwilioAccount.Format}.";

    // Null when the pasted value is malformed or the deployment's integration is not Ready.
    private async Task<TwilioAccount?> AccountAsync(Credential credential, CancellationToken ct)
    {
        if (Snapshot.AuthMethodId != DeploymentMethod) { return TwilioAccount.TryParse(credential.Value); }
        var handle = integrations.Get(TwilioModule.Integration.Id);
        var snapshot = await handle.ReadAsync(ct);
        if (snapshot.Status != IntegrationStatus.Ready
            || !snapshot.Settings.TryGetValue("AccountSid", out var sid) || string.IsNullOrWhiteSpace(sid)
            || !snapshot.Settings.TryGetValue("FromNumber", out var from) || string.IsNullOrWhiteSpace(from))
        { return null; }
        return new TwilioAccount(sid.Trim(), await handle.ReadSecretAsync("AuthToken", ct), from.Trim());
    }

    private void RequireOwnBrain()
    {
        (string Brain, string Connector, string Connection) key;
        try { key = Key; }
        catch (ArgumentException) { throw new UnauthorizedAccessException("A canonical connection key is required."); }
        if (key.Brain != BrainScope.CurrentId()) { throw new UnauthorizedAccessException("The connection belongs to another brain."); }
    }
}
