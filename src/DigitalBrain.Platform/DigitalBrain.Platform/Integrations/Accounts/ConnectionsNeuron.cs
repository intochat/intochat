using DigitalBrain;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using DigitalBrain.Framework.Platform.Secrets;
using DigitalBrain.Framework.Types;
using DigitalBrain.Platform.Secrets;
using DigitalBrain.Types;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Integrations.Accounts;

// The owner's account registry. A credential is written to the shared secrets grain and the
// record holds only its SecretRef; the value is released only inside the read-only probe.
[GrainType(ConnectionNames.NeuronType)] // alias predates the integrations rename; persisted, do not touch
internal sealed class ConnectionsNeuron : Neuron<ConnectionsState>, IConnectionRegistry
{
    private readonly IGrainFactory _grains;
    private readonly ConnectionCredentialProbe _probe;
    private readonly TimeProvider _time;

    public ConnectionsNeuron(
        [PersistentState("connections", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<ConnectionsState> store,
        IGrainFactory grains,
        ConnectionCredentialProbe probe,
        TimeProvider time) : base(store)
    {
        _grains = grains;
        _probe = probe;
        _time = time;
    }

    public Task<Connection[]> List(CancellationToken cancellationToken = default)
    {
        var records = Snapshot.Connections.Values
            .Select(connection => ConnectionAuthentication.Normalize(connection, ServiceProvider.GetRequiredService<IReadOnlyList<ConnectorDefinition>>()))
            .OrderBy(record => record.Id, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(records);
    }

    public async Task<Connection> Connect(ConnectConnection request, CancellationToken cancellationToken = default)
    {
        var caller = CallerForWrite();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IntegrationId);
        var source = request.IntegrationId.Trim();
        var connector = (await ServiceProvider.GetRequiredService<ConnectorCatalog>().ListAsync(cancellationToken))
            .SingleOrDefault(c => c.Id == source) ?? throw new ArgumentException("This connector is not offered by the deployment.");
        var method = request.AuthMethodId;
        if (string.IsNullOrEmpty(method) && connector.AuthMethods.Length == 1) { method = connector.AuthMethods[0]; }
        if (!connector.AuthMethods.Contains(method, StringComparer.Ordinal)) { throw new ArgumentException("Choose a supported authentication method."); }
        if (string.IsNullOrWhiteSpace(request.ConnectionId))
        {
            throw new ArgumentException("A connection id is required.", nameof(request));
        }

        var credential = await StoreOrReferenceAsync(source, request, caller, cancellationToken);
        var result = await _probe.ProbeAsync(source, credential, caller, cancellationToken);
        var record = new Connection
        {
            Id = request.ConnectionId,
            IntegrationId = source,
            WorkspaceId = caller.BrainId,
            Credential = credential,
            Status = ToStatus(result.Outcome),
            LastProbedAt = _time.GetUtcNow(),
            AuthMethodId = method,
        };
        Snapshot.Connections[record.Id] = record;
        await Save(Snapshot, new ConnectionStatusChanged(record.Id, record.IntegrationId, record.Status));
        return record;
    }

    public async Task<Connection> Probe(string connectionId, CancellationToken cancellationToken = default)
    {
        var caller = CallerForWrite();
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        if (!Snapshot.Connections.TryGetValue(connectionId, out var current))
        {
            throw new ConnectionNotConfiguredException($"Connection '{connectionId}' is not configured.");
        }

        current = ConnectionAuthentication.Normalize(current, ServiceProvider.GetRequiredService<IReadOnlyList<ConnectorDefinition>>());
        if (current.Status == ConnectionStatus.Disconnected)
        { throw new ConnectionNotConfiguredException("Reconnect this connection using a supported authentication method."); }

        var result = await _probe.ProbeAsync(current.IntegrationId, current.Credential, caller, cancellationToken);
        var record = current with { Status = ToStatus(result.Outcome), LastProbedAt = _time.GetUtcNow() };
        Snapshot.Connections[connectionId] = record;
        await Save(Snapshot, new ConnectionStatusChanged(record.Id, record.IntegrationId, record.Status));
        return record;
    }

    public async Task Disconnect(string connectionId, CancellationToken cancellationToken = default)
    {
        CallerForWrite();
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        if (!Snapshot.Connections.Remove(connectionId))
        {
            return;
        }

        await Save(Snapshot, new ConnectionDisconnected(connectionId));
    }

    private async Task<SecretRef> StoreOrReferenceAsync(string source, ConnectConnection request, CallerContext caller, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.SecretReference))
        {
            if (!SecretReferences.IsReference(request.SecretReference) || !OwnedByCaller(request.SecretReference, caller))
            {
                throw new ArgumentException("SecretReference must be an existing reference into your own vault.", nameof(request));
            }

            return new SecretRef { Reference = request.SecretReference, Label = request.Label ?? source, Status = SecretStatus.Set };
        }

        if (string.IsNullOrWhiteSpace(request.Value))
        {
            throw new ArgumentException("Provide a connection value or an existing vault secret reference.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(caller.PrincipalId))
        {
            throw new ArgumentException("A principal is required to store a connection credential.", nameof(caller));
        }

        var vault = _grains.GetGrain<ISecrets>(caller.PrincipalId);
        var fieldPath = FieldPath(this.GetPrimaryKeyString(), source, request.ConnectionId);
        return await vault.Set(caller, fieldPath, request.Label ?? source, request.Value, cancellationToken);
    }

    // The vault owner named by a reference must be the caller's own principal; another owner's vault,
    // the platform vault included, is never bindable.
    private static bool OwnedByCaller(string reference, CallerContext caller)
    {
        try { return new SecretRef { Reference = reference }.OwnerOf() == caller.PrincipalId; }
        catch (InvalidOperationException) { return false; }
    }

    // The ambient stamp is the only trusted caller. Installed apps run under an App stamp and may not
    // reach the account registry whatever else they pass.
    private static CallerContext CallerForWrite()
    {
        var caller = CallerContextStamper.Require();
        if (caller.Kind == CallerKind.App || caller.StampedBy == TrustedEdge.AppProxy)
        {
            throw new InvalidOperationException("Installed apps cannot manage integration accounts.");
        }

        return caller;
    }

    private static ConnectionStatus ToStatus(ConnectionProbeOutcome outcome) => outcome switch
    {
        ConnectionProbeOutcome.Connected => ConnectionStatus.Connected,
        ConnectionProbeOutcome.Expired => ConnectionStatus.Expired,
        _ => ConnectionStatus.Failing,
    };

    private static string FieldPath(string registry, string source, string connectionId) => "connections." +
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new[] { registry, source, connectionId }))));
}
