using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Requests;
using IntoChat.OS;
using IntoChat.OS.Authority.Identity.Rules;
using IntoChat.OS.Authority.Integrations;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Connections;

public sealed class ConnectionRequestsState
{
    public ConnectionRef[] Pending { get; set; } = [];
    public long Revision { get; set; }

    // The brain's connection order (Settings order) by connection key; connections not listed follow in index order (05-07).
    public string[] Order { get; set; } = [];
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

    public async Task Changed(string connectionKey)
    {
        await Check("Changed", new ConnectionBelongsToBrain(ConnectionKeys.Parse(connectionKey).BrainId, BrainId));
        var next = new ConnectionRequestsState { Pending = State.Pending, Revision = State.Revision + 1, Order = State.Order };
        await Save(next, new ConnectionsChanged(next.Revision));
    }

    public async Task<ConnectorKind[]> ListKinds()
        => (await catalog.ListAsync(CancellationToken.None)).Select(definition => new ConnectorKind(definition.Id, definition.DisplayName)).ToArray();

    public Task<ConnectionRef[]> ListPending() => Task.FromResult<ConnectionRef[]>([.. State.Pending]);

    public async Task<ConnectionSummary[]> ListConnections()
    {
        var result = new List<ConnectionSummary>();
        var plain = Context.Services;
        foreach (var key in Ordered(await Get<IConnectionIndex>(IConnectionIndex.Id(BrainId)).List()))
        {
            var identity = ConnectionKeys.Parse(key);
            if (!connectors.Any(connector => connector.Id == identity.ConnectorId)) { continue; }
            var connection = await plain.GetConnection(key).Read();
            result.Add(new(connection.ConnectorId, connection.ConnectionId, key,
                connection.Status.ToString().ToLowerInvariant(), connection.Account, connection.LastProbedAt)
            { Provides = Provides(identity.ConnectorId), Order = result.Count });
        }
        return [.. result];
    }

    public async Task Reorder(string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var known = (await Get<IConnectionIndex>(IConnectionIndex.Id(BrainId)).List()).ToHashSet(StringComparer.Ordinal);
        var listed = keys.Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        var order = listed.Concat(Ordered(known).Where(key => !listed.Contains(key, StringComparer.Ordinal))).ToArray();
        if (order.SequenceEqual(State.Order, StringComparer.Ordinal)) { return; }
        var next = new ConnectionRequestsState { Pending = State.Pending, Revision = State.Revision + 1, Order = order };
        await Save(next, new ConnectionsChanged(next.Revision));
    }

    // Settings order: the stored order first, then every other key in index order (new connections are appended).
    private IEnumerable<string> Ordered(IEnumerable<string> keys)
    {
        var all = keys.ToList();
        var present = all.ToHashSet(StringComparer.Ordinal);
        return State.Order.Where(present.Contains).Concat(all.Where(key => !State.Order.Contains(key, StringComparer.Ordinal))).Distinct(StringComparer.Ordinal);
    }

    // What the connection offers: its module's ModuleManifest.Provides, kept only where the connector's neuron implements the
    // capability's contract or an accepted binding (a module serving two connectors provides through the one that implements it).
    private string[] Provides(string connector)
    {
        if (Context.Services.GetService<ConnectionNeuronTypes>() is not { } hosted || hosted.NeuronOf(connector) is not { } neuron) { return []; }
        var registry = Context.Services.GetService<DigitalBrain.Runtime.Orleans.Capabilities.CapabilityRegistry>();
        return [.. hosted.ProvidesOf(connector)
            .Where(capability => registry?.BindingsOf(capability) is not { Count: > 0 } bindings || bindings.Any(binding => binding.IsAssignableFrom(neuron)))
            .Select(capability => capability.ToString())];
    }

    public async Task<ConnectionRef> Request(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var id = kind.Trim();
        var available = (await catalog.ListAsync(CancellationToken.None)).Any(definition => definition.Id == id);
        await Check("Request", new IntegrationIsAvailable(kind, available));

        var account = new ConnectionRef(id, Guid.NewGuid().ToString("n"));
        var next = new ConnectionRequestsState { Pending = [.. State.Pending, account], Revision = State.Revision, Order = State.Order };
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
