using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Monnet.Signals;

namespace DigitalBrain.Monnet;

// Served only on connection keys {brain}/connection/monnet/{id}. The `credentials` method serves the
// merchant pasted into the sealed state; the `deployment` method serves the deployment's merchant from
// the monnet integration (its sealed value is ignored).
[GrainType("monnet")]
internal sealed class MonnetNeuron(
    IMonnetProvider provider, MonnetModuleOptions options, IModuleIntegrations<MonnetModule> integrations, TimeProvider time,
    [PersistentState("monnet", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<MonnetState> store)
    : ConnectionNeuron<MonnetState>(store), IMonnet, IMonnetInbound
{
    public const string DeploymentMethod = "deployment";
    private const int MaxPending = 200;
    private const int MaxReceived = 500;
    private const int MaxSeen = 5000;
    private static readonly string[] Paid = ["paid", "succeeded", "completed", "approved"];
    private static readonly string[] Unpaid = ["failed", "rejected", "expired", "cancelled", "canceled"];

    private bool IsDeployment => Snapshot.AuthMethodId == DeploymentMethod;

    public async Task<PaymentOrder> CreatePaymentOrder(CreatePaymentOrder command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Amount <= 0) { throw new ArgumentException("A payment order needs a positive amount.", nameof(command)); }
        if (string.IsNullOrWhiteSpace(command.Reference) || command.Reference.Length > 64)
        { throw new ArgumentException("A payment order needs a reference of at most 64 characters.", nameof(command)); }
        if (command.Currency is not { Length: 3 } currency || !currency.All(char.IsAsciiLetterUpper))
        { throw new ArgumentException("The currency is an ISO 4217 code such as PEN.", nameof(command)); }

        PaymentOrder order;
        try { order = await WithAccount((account, ct) => provider.CreatePaymentOrderAsync(account, command, ct)); }
        catch (Exception error) when (error is MonnetUnavailableException or MonnetNotConnectedException)
        {
            await Fail(command.Reference, error.Message);
            throw;
        }
        Snapshot.Pending = [order, .. Snapshot.Pending.Where(pending => pending.PaymentId != order.PaymentId).Take(MaxPending - 1)];
        Snapshot.LastFailure = null;
        await Save(Snapshot, new PaymentOrderCreated(order));
        return order;
    }

    public async Task<PaymentStatus> ReadPayment(ReadPayment query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.PaymentId);
        try { return await WithAccount((account, ct) => provider.ReadPaymentAsync(account, query.PaymentId, ct)); }
        catch (Exception error) when (error is MonnetUnavailableException or MonnetNotConnectedException)
        {
            await Fail(query.PaymentId, error.Message);
            throw;
        }
    }

    public Task<PaymentReceipt[]> ReadReceived(int limit)
    {
        RequireOwnBrain();
        return Task.FromResult(Snapshot.Received.Take(Math.Clamp(limit, 1, 100)).ToArray());
    }

    // Only a payment's final outcome is a signal; Monnet may deliver it more than once. Every Monnet
    // connection of the brain is offered the delivery; only the one configured for its merchant takes it.
    // The route already authenticated the delivery with the webhook secret, so a failed outbound probe
    // (the API key, not the webhook) does not drop a payment the merchant was paid for.
    public async Task AcceptWebhook(MonnetWebhookPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (Snapshot.Status == ConnectionStatus.Disconnected || Snapshot.Account is null
            || !string.Equals(Snapshot.Account, payload.MerchantId, StringComparison.Ordinal)) { return; }
        if (Snapshot.Seen.Contains(payload.PaymentId, StringComparer.Ordinal)) { return; }
        var status = (payload.Status ?? "").Trim().ToLowerInvariant();
        var paid = Paid.Contains(status);
        if (!paid && !Unpaid.Contains(status)) { return; }

        Snapshot.Seen = [.. Snapshot.Seen.Skip(Math.Max(0, Snapshot.Seen.Length - (MaxSeen - 1))), payload.PaymentId];
        Snapshot.Pending = Snapshot.Pending.Where(pending => pending.PaymentId != payload.PaymentId).ToArray();
        if (!paid)
        {
            Snapshot.LastFailure = $"payment {status}";
            await Save(Snapshot, new PaymentFailed(payload.Reference, Snapshot.LastFailure));
            return;
        }
        var receipt = new PaymentReceipt(payload.PaymentId, payload.Reference, payload.Amount, payload.Currency, status, time.GetUtcNow());
        Snapshot.Received = [receipt, .. Snapshot.Received.Take(MaxReceived - 1)];
        await Save(Snapshot, new PaymentReceived(receipt));
    }

    // Monnet, not the paste or the configuration, says whether the key works.
    protected override async Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        MonnetAccount account;
        try { account = await ResolveAccountAsync(credential, ct); }
        catch (Exception error) when (error is MonnetUnavailableException or MonnetNotConnectedException) { return ConnectionProbeResult.Failing(error.Message); }
        return await provider.ProbeAsync(account, ct) is { } detail ? ConnectionProbeResult.Failing(detail) : ConnectionProbeResult.Connected();
    }

    // Connect seals any value; a `credentials` paste that is not MerchantId=…;ApiKey=… is refused here and
    // left Failing, as is a `deployment` connection when the deployment has no ready monnet integration.
    protected override async Task OnConnectedAsync()
    {
        string? merchant;
        string failure;
        if (IsDeployment)
        {
            merchant = await DeploymentMerchantAsync(CancellationToken.None);
            failure = new MonnetNotConnectedException().Message;
        }
        else
        {
            merchant = await UseCredentialAsync((credential, _) =>
                Task.FromResult(MonnetCredentials.TryParse(credential.Value, out var parsed) ? parsed.MerchantId : null), CancellationToken.None);
            failure = MonnetCredentials.Malformed;
        }
        if (merchant is null)
        {
            await MarkFailing(failure);
            throw new ArgumentException(failure);
        }
        Snapshot.Account = merchant;
        await SaveQuietly(Snapshot);
    }

    private async Task Fail(string reference, string detail)
    {
        Snapshot.LastFailure = detail;
        await Save(Snapshot, new PaymentFailed(reference, detail));
    }

    private async Task<T> WithAccount<T>(Func<MonnetAccount, CancellationToken, Task<T>> call)
    {
        RequireOwnBrain();
        if (Snapshot.Status == ConnectionStatus.Disconnected || Snapshot.Secrets.Length == 0) { throw new MonnetNotConnectedException(); }
        return await UseCredentialAsync(async (credential, ct) => await call(await ResolveAccountAsync(credential, ct), ct), CancellationToken.None);
    }

    private async Task<MonnetAccount> ResolveAccountAsync(Credential credential, CancellationToken ct)
    {
        if (!IsDeployment)
        {
            return MonnetCredentials.TryParse(credential.Value, out var credentials)
                ? Account(credentials)
                : throw new MonnetUnavailableException(MonnetCredentials.Malformed);
        }
        var integration = integrations.Get("monnet");
        var snapshot = await integration.ReadAsync(ct);
        if (snapshot.Status != IntegrationStatus.Ready || !snapshot.Settings.TryGetValue("MerchantId", out var merchant) || string.IsNullOrWhiteSpace(merchant))
        { throw new MonnetNotConnectedException(); }
        Uri? baseUrl = null;
        if (snapshot.Settings.TryGetValue("BaseUrl", out var configured) && configured.Length > 0
            && !Uri.TryCreate(configured, UriKind.Absolute, out baseUrl))
        { throw new MonnetUnavailableException("The Monnet integration's BaseUrl is not an absolute URL."); }
        var apiKey = await integration.ReadSecretAsync("ApiKey", ct);
        return new MonnetAccount(options.Resolve(baseUrl), merchant, apiKey);
    }

    private async Task<string?> DeploymentMerchantAsync(CancellationToken ct)
    {
        var snapshot = await integrations.Get("monnet").ReadAsync(ct);
        return snapshot.Status == IntegrationStatus.Ready && snapshot.Settings.TryGetValue("MerchantId", out var merchant) && !string.IsNullOrWhiteSpace(merchant)
            ? merchant : null;
    }

    private MonnetAccount Account(MonnetCredentials credentials)
        => new(options.Resolve(credentials.BaseUrl), credentials.MerchantId, credentials.ApiKey);

    private void RequireOwnBrain()
    {
        (string Brain, string Connector, string Connection) key;
        try { key = Key; }
        catch (ArgumentException) { throw new UnauthorizedAccessException("A canonical connection key is required."); }
        if (key.Connector != "monnet" || key.Brain != BrainScope.CurrentId())
        { throw new UnauthorizedAccessException("The connection belongs to another brain."); }
    }
}
