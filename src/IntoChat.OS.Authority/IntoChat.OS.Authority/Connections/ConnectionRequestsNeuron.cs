using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Integrations;
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
    private AuthorityNeurons Platform => new(GrainFactory);

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
            if (!connectors.Any(connector => connector.Id == identity.ConnectorId)) { continue; }
            var connection = await GrainFactory.GetConnection(key).Read();
            result.Add(new(connection.ConnectorId, connection.ConnectionId, key,
                connection.Status.ToString().ToLowerInvariant(), connection.Account, connection.LastProbedAt));
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

    public async Task<ConnectUrl> LoginUrl(string connectorId)
    {
        var brain = this.GetPrimaryKeyString();
        if (BrainScope.CurrentId() != brain) { return new(null, "Connections are made only in your own brain."); }
        var connector = connectors.FirstOrDefault(declared => declared.Id == connectorId);
        var unavailable = $"{connector?.DisplayName ?? connectorId} is not available on this host.";
        if (connector?.OAuthMethods.FirstOrDefault() is not { } method) { return new(null, unavailable); }
        // The gallery keeps one connection per connector, named by the connector.
        var start = await ConnectionLogin.StartAsync(ServiceProvider, brain, connector.Id, connector.Id, method.Id, null, CancellationToken.None);
        return start.Url is { } url ? new(url.AbsoluteUri, null) : new(null, start.Unavailable ? unavailable : start.Error);
    }
}
