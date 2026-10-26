using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Integrations;
using IntoChat.OS.Authority.Integrations;
using Orleans.Concurrency;

namespace IntoChat.OS.Authority.Connections;

[GenerateSerializer, Alias("integrations.script-accounts")]
public sealed class ConnectionRequestsState
{
    [Id(0)] public ConnectionRef[] Pending { get; set; } = [];
    [Id(1)] public long Revision { get; set; }
}

// Script-facing connection requests: a script asks for a connector kind; the user connects it.
[GrainType("integrations.accounts")]
internal sealed class ConnectionRequestsNeuron(
    [PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<ConnectionRequestsState> store,
    ConnectorCatalog catalog,
    IReadOnlyList<ConnectorDefinition> connectors)
    : Neuron<ConnectionRequestsState>(store), IConnectionRequests, IConnectionCatalogUpdates
{
    private AuthorityNeurons Platform => ServiceProvider.GetRequiredService<AuthorityNeurons>();

    public Task Changed(string connectionKey)
    {
        if (ConnectionKeys.Parse(connectionKey).BrainId != this.GetPrimaryKeyString())
        { throw new ArgumentException("The connection belongs to another brain.", nameof(connectionKey)); }
        var next = Snapshot;
        next.Revision++;
        return Save(next, new ConnectionsChanged(next.Revision));
    }

    [ReadOnly]
    public async Task<ConnectorKind[]> ListKinds()
        => (await catalog.ListAsync(CancellationToken.None)).Select(definition => new ConnectorKind(definition.Id, definition.DisplayName)).ToArray();

    [ReadOnly]
    public Task<ConnectionRef[]> ListPending() => Task.FromResult<ConnectionRef[]>([.. Snapshot.Pending]);

    [ReadOnly]
    public async Task<ConnectionSummary[]> ListConnections()
    {
        var result = new List<ConnectionSummary>();
        foreach (var key in await Platform.Connections(this.GetPrimaryKeyString()).List())
        {
            var identity = ConnectionKeys.Parse(key);
            if (connectors.FirstOrDefault(connector => connector.Id == identity.ConnectorId) is not { } declared) { continue; }
            var connection = await ServiceProvider.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>().GetConnection(key).Read();
            result.Add(new(connection.ConnectorId, connection.ConnectionId, key,
                connection.Status.ToString().ToLowerInvariant(), connection.Account, connection.LastProbedAt)
            { Capabilities = [.. declared.Capabilities] });
        }
        return [.. result];
    }

    public async Task<ConnectionRef> Request(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var id = kind.Trim();
        if (!(await catalog.ListAsync(CancellationToken.None)).Any(definition => definition.Id == id))
        {
            throw new ArgumentException($"Integration '{kind}' is not available.");
        }

        var account = new ConnectionRef(id, Guid.NewGuid().ToString("n"));
        var next = Snapshot;
        next.Pending = [.. next.Pending, account];
        await Save(next, new ConnectionRequested(account.Kind, account.Id));
        return account;
    }

    [ReadOnly]
    public async Task<ConnectorOffer[]> ListOffers()
    {
        var offered = (await catalog.ListAsync(CancellationToken.None)).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var connected = (await ListConnections()).Where(row => row.Status == "connected").Select(row => row.Kind).ToHashSet(StringComparer.Ordinal);
        return [.. connectors.Where(connector => connector.OAuthMethods.Length > 0)
            .Select(connector => new ConnectorOffer(connector.Id, connector.DisplayName, offered.Contains(connector.Id), connected.Contains(connector.Id)))];
    }
}
