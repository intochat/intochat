using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Integrations;
using DigitalBrain.Platform.Integrations;
using Orleans.Concurrency;

namespace DigitalBrain.Platform.Connections;

[GenerateSerializer, Alias("integrations.script-accounts")]
public sealed class ConnectionRequestsState
{
    [Id(0)] public ConnectionRef[] Pending { get; set; } = [];
}

// Script-facing connection requests: a script asks for a connector kind; the user connects it.
[GrainType("integrations.accounts")]
internal sealed class ConnectionRequestsNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<ConnectionRequestsState> store,
    ConnectorCatalog catalog)
    : Neuron<ConnectionRequestsState>(store), IConnectionRequests
{
    [ReadOnly]
    public async Task<ConnectorKind[]> ListKinds()
        => (await catalog.ListAsync(CancellationToken.None)).Select(definition => new ConnectorKind(definition.Id, definition.DisplayName)).ToArray();

    [ReadOnly]
    public Task<ConnectionRef[]> ListPending() => Task.FromResult<ConnectionRef[]>([.. Snapshot.Pending]);

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
}
