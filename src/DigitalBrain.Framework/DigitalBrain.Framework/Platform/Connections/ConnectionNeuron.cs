using DigitalBrain.Framework.Platform.Connections.Signals;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Framework.Platform.Connections;

// Every connector's connection neuron derives from this. The credential is sealed state; the hooks
// receive it opened for one provider call inside the grain turn and never return it.
public abstract class ConnectionNeuron<TState>(IPersistentState<TState> store) : Neuron<TState>(store), IConnection, IConnectionInbound
    where TState : ConnectionState
{
    private const string ValueSecret = "value";
    private const string AccessSecret = "access";
    private const string RefreshSecret = "refresh";
    private static readonly TimeSpan RefreshLead = TimeSpan.FromSeconds(30);

    private (string Brain, string Connector, string Connection)? _key;
    protected (string Brain, string Connector, string Connection) Key => _key ??= ConnectionKeys.Parse(this.GetPrimaryKeyString());

    // The brain in the key decides who may call; a connector cannot widen it.
    public sealed override NeuronAccess Access(string operation) => base.Access(operation);

    private ISecretSealer Sealer => ServiceProvider.GetRequiredService<ISecretSealer>();
    private TimeProvider Clock => ServiceProvider.GetRequiredService<TimeProvider>();
    private ConnectorDefinition Connector => ServiceProvider.GetRequiredService<IReadOnlyList<ConnectorDefinition>>()
        .FirstOrDefault(c => c.Id == Key.Connector) ?? throw new InvalidOperationException($"No composed module declares connector '{Key.Connector}'.");

    // The module answers: does this credential still work?
    protected abstract Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct);

    // The module may parse provider fields (instance url, mailbox) and refuse a grant.
    protected virtual Task<OAuthGrant> AcceptGrantAsync(OAuthGrant grant) => Task.FromResult(grant);

    // Standard refresh_token grant through the Platform; GitHub overrides.
    protected virtual Task<OAuthGrant> RefreshAsync(Credential credential, CancellationToken ct)
        => credential.Refresh is null
            ? throw new InvalidOperationException("The connection has no refresh token.")
            : ServiceProvider.GetRequiredService<IOAuthTokenExchange>().RefreshAsync(Key.Connector, Snapshot.AuthMethodId, credential.Refresh, ct);

    protected virtual Task OnConnectedAsync() => Task.CompletedTask;
    protected virtual Task OnDisconnectedAsync() => Task.CompletedTask;

    public Task<ConnectionView> Read() => Task.FromResult(View());

    public async Task<ConnectionView> Connect(string authMethodId, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        RequireMember();
        RequireAuthMethod(authMethodId);
        Snapshot.AuthMethodId = authMethodId;
        Snapshot.Secrets = [Sealer.Seal(ValueSecret, value)];
        Snapshot.ExpiresAt = null;
        Snapshot.Scopes = "";
        await Transition(ConnectionStatus.Connected, null);
        await Track();
        await OnConnectedAsync();
        return View();
    }

    public async Task<ConnectionView> AcceptGrant(OAuthGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        RequireMember();
        RequireAuthMethod(grant.AuthMethodId);
        // The module's override sets Snapshot.Account (mailbox, instance url, owner/repo) and may throw.
        var accepted = await AcceptGrantAsync(grant);
        Seal(accepted);
        Snapshot.AuthMethodId = accepted.AuthMethodId;
        await Transition(ConnectionStatus.Connected, null);
        await Track();
        await OnConnectedAsync();
        return View();
    }

    public async Task<ConnectionView> Probe()
    {
        RequireMember();
        if (Snapshot.Status == ConnectionStatus.Disconnected) { throw new InvalidOperationException("The connection is not configured."); }
        ConnectionProbeResult result;
        try { result = await ProbeAsync(Open(), CancellationToken.None); }
        // The message may carry a provider URL with a token in it; only the type is kept.
        catch (Exception error) when (error is not OperationCanceledException)
        { result = ConnectionProbeResult.Failing($"The connector probe failed ({error.GetType().Name})."); }
        Snapshot.LastProbedAt = Clock.GetUtcNow();
        await Transition(result.Outcome switch
        {
            ConnectionProbeOutcome.Connected => ConnectionStatus.Connected,
            ConnectionProbeOutcome.Expired => ConnectionStatus.Expired,
            _ => ConnectionStatus.Failing,
        }, result.Detail);
        return View();
    }

    public async Task Disconnect()
    {
        RequireMember();
        // The hook runs while Account is still known, so an index keyed by it can be cleaned.
        await OnDisconnectedAsync();
        Snapshot.Secrets = [];
        Snapshot.ExpiresAt = null;
        Snapshot.Account = null;
        await Transition(ConnectionStatus.Disconnected, null);
        await GrainFactory.GetGrain<IConnectionIndex>(Key.Brain).Untrack(this.GetPrimaryKeyString());
    }

    // Opens the credential for one call; refreshes first when the access token is about to expire.
    protected async Task<T> UseCredentialAsync<T>(Func<Credential, CancellationToken, Task<T>> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (Snapshot.Status == ConnectionStatus.Disconnected || Snapshot.Secrets.Length == 0)
        { throw new InvalidOperationException($"{Key.Connector}/{Key.Connection} is not connected."); }
        var credential = Open();
        if (Snapshot.ExpiresAt is { } expiry && expiry <= Clock.GetUtcNow() + RefreshLead && credential.Refresh is not null)
        {
            var refreshed = await RefreshAsync(credential, ct);
            Seal(refreshed);
            await Transition(ConnectionStatus.Connected, null);
            credential = Open();
        }
        return await call(credential, ct);
    }

    protected Task MarkExpired(string detail) => Transition(ConnectionStatus.Expired, detail);
    protected Task MarkFailing(string detail) => Transition(ConnectionStatus.Failing, detail);

    private Credential Open()
    {
        string? value = null, refresh = null;
        foreach (var secret in Snapshot.Secrets)
        {
            if (secret.Name is ValueSecret or AccessSecret) { value = Sealer.Open(secret); }
            else if (secret.Name == RefreshSecret) { refresh = Sealer.Open(secret); }
        }
        return new(value ?? throw new InvalidOperationException("The connection holds no credential."), refresh);
    }

    private void Seal(OAuthGrant grant)
    {
        Snapshot.Secrets = grant.RefreshToken is null
            ? [Sealer.Seal(AccessSecret, grant.AccessToken)]
            : [Sealer.Seal(AccessSecret, grant.AccessToken), Sealer.Seal(RefreshSecret, grant.RefreshToken)];
        Snapshot.ExpiresAt = grant.ExpiresAt;
        Snapshot.Scopes = grant.Scopes;
    }

    // One function moves status and publishes, so no module can publish a status it did not persist.
    // Every operation that calls it publishes exactly one signal, even when the status repeats.
    private Task Transition(ConnectionStatus status, string? detail)
    {
        Snapshot.Status = status;
        Snapshot.Detail = detail;
        Signal signal = status switch
        {
            ConnectionStatus.Connected => new ConnectionConnected(Key.Connector, Key.Connection, Snapshot.AuthMethodId, Snapshot.Account),
            ConnectionStatus.Expired => new ConnectionExpired(Key.Connector, Key.Connection),
            ConnectionStatus.Failing => new ConnectionFailing(Key.Connector, Key.Connection, detail ?? ""),
            _ => new ConnectionDisconnected(Key.Connector, Key.Connection),
        };
        return Save(Snapshot, signal);
    }

    private Task Track() => GrainFactory.GetGrain<IConnectionIndex>(Key.Brain).Track(this.GetPrimaryKeyString());

    private void RequireAuthMethod(string authMethodId)
    {
        if (!Connector.AuthMethods.Contains(authMethodId, StringComparer.Ordinal))
        { throw new ArgumentException($"Connector '{Key.Connector}' does not offer authentication method '{authMethodId}'.", nameof(authMethodId)); }
    }

    // Members of the brain named in the key; never an installed app.
    private void RequireMember()
    {
        CallerContext caller;
        try { caller = CallerContextStamper.Require(); }
        catch (UntrustedCallerException error) { throw new UnauthorizedAccessException(error.Message, error); }
        if (caller.Kind == CallerKind.App || caller.StampedBy == TrustedEdge.AppProxy
            || BrainScope.Create(caller.AccountId, caller.BrainId).Id != Key.Brain)
        { throw new UnauthorizedAccessException($"Only a member of the owning brain can manage connection {Key.Connector}/{Key.Connection}."); }
    }

    private ConnectionView View() => new(Key.Connector, Key.Connection, Snapshot.AuthMethodId, Snapshot.Status, Snapshot.Account, Snapshot.ExpiresAt, Snapshot.LastProbedAt, Snapshot.Detail);
}
