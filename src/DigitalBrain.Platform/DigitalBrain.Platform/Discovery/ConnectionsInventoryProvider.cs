using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations.Accounts;

namespace DigitalBrain.Platform.Discovery;

// Connections are entries of one per-brain grain. Their identity and liveness are that
// aggregate's; display identifies the entry without exposing its credential reference.
internal sealed class ConnectionsInventoryProvider(IDigitalBrain brain, IActivationCensus census) : IRegistryInventoryProvider
{
    public string Domain => "connections";
    public async Task<NeuronRecord[]> Persisted(string? typeId, CancellationToken ct)
    {
        var scope = BrainScope.CurrentId();
        if (typeId is not null && typeId != ConnectionNames.NeuronType) { return []; }
        return (await brain.Get<IConnectionRegistry>(scope).List(ct)).Select(connection =>
            new NeuronRecord(Domain, ConnectionNames.NeuronType, scope, connection.IntegrationId + "/" + connection.Id)).ToArray();
    }
    public async Task<NeuronRecord[]> Active(string? typeId, CancellationToken ct)
    {
        var scope = BrainScope.CurrentId();
        if (typeId is not null && typeId != ConnectionNames.NeuronType) { return []; }
        var live = await census.Activations(ConnectionNames.NeuronType, ct);
        return live.Any(record => record.Key == scope) ? await Persisted(typeId, ct) : [];
    }
}
