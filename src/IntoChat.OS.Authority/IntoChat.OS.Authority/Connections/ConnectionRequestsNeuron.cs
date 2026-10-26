using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Integrations;
using IntoChat.OS.Authority.Integrations;

namespace IntoChat.OS.Authority.Connections;

public sealed class ConnectionRequestsState
{
    public ConnectionRef[] Pending { get; set; } = [];
    public long Revision { get; set; }
}

// Script-facing connection requests: a script asks for a connector kind; the user connects it.
// A plain neuron (06-08), one per brain at "{brain}/main".
[DigitalBrain.Neuron(IConnectionCatalogUpdates.TypeName)]
internal sealed class ConnectionRequestsNeuron(
    ConnectorCatalog catalog,
    IReadOnlyList<ConnectorDefinition> connectors)
    : DigitalBrain.Neuron<ConnectionRequestsState>, IConnectionRequests, IConnectionCatalogUpdates
{
    private string BrainId => Id.Brain.Value;

    public Task Changed(string connectionKey)
    {
        if (ConnectionKeys.Parse(connectionKey).BrainId != BrainId)
        { throw new ArgumentException("The connection belongs to another brain.", nameof(connectionKey)); }
        var next = new ConnectionRequestsState { Pending = State.Pending, Revision = State.Revision + 1 };
        return Save(next, new ConnectionsChanged(next.Revision));
    }

    public async Task<ConnectorKind[]> ListKinds()
        => (await catalog.ListAsync(CancellationToken.None)).Select(definition => new ConnectorKind(definition.Id, definition.DisplayName)).ToArray();

    public Task<ConnectionRef[]> ListPending() => Task.FromResult<ConnectionRef[]>([.. State.Pending]);

    public async Task<ConnectionSummary[]> ListConnections()
    {
        var result = new List<ConnectionSummary>();
        var plain = Context.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>();
        foreach (var key in await Get<IConnectionIndex>(IConnectionIndex.Id(BrainId)).List())
        {
            var identity = ConnectionKeys.Parse(key);
            if (connectors.FirstOrDefault(connector => connector.Id == identity.ConnectorId) is not { } declared) { continue; }
            var connection = await plain.GetConnection(key).Read();
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
        var next = new ConnectionRequestsState { Pending = [.. State.Pending, account], Revision = State.Revision };
        await Save(next, new ConnectionRequested(account.Kind, account.Id));
        return account;
    }

    public async Task<ConnectorOffer[]> ListOffers()
    {
        var offered = (await catalog.ListAsync(CancellationToken.None)).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var connected = (await ListConnections()).Where(row => row.Status == "connected").Select(row => row.Kind).ToHashSet(StringComparer.Ordinal);
        return [.. connectors.Where(connector => connector.OAuthMethods.Length > 0)
            .Select(connector => new ConnectorOffer(connector.Id, connector.DisplayName, offered.Contains(connector.Id), connected.Contains(connector.Id)))];
    }
}
