using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;

namespace DigitalBrain.Platform.Connections;

// The ambient brain's connections, as its index lists them. Each record is a connection neuron
// (grain type = connector id, key = connection key), built from the key alone: listing activates
// no connection, so Active can ask the census which ones are live.
internal sealed class ConnectionsInventoryProvider(IGrainFactory grains, IActivationCensus census, IReadOnlyList<ConnectorDefinition> connectors) : IRegistryInventoryProvider
{
    public string Domain => "connections";

    public async Task<NeuronRecord[]> Persisted(string? typeId, CancellationToken ct)
    {
        var records = new List<NeuronRecord>();
        foreach (var key in await grains.GetGrain<IConnectionIndex>(BrainScope.CurrentId()).List().WaitAsync(ct))
        {
            var (_, connector, connection) = ConnectionKeys.Parse(key);
            // A connector no longer composed has no neuron to address; its key is skipped, not fatal.
            if (!connectors.Any(c => c.Id == connector) || (typeId is not null && typeId != connector)) { continue; }
            records.Add(new NeuronRecord(Domain, connector, key, connector + "/" + connection));
        }

        return [.. records];
    }

    public async Task<NeuronRecord[]> Active(string? typeId, CancellationToken ct)
    {
        var active = new List<NeuronRecord>();
        foreach (var group in (await Persisted(typeId, ct)).GroupBy(record => record.TypeId))
        {
            var live = (await census.Activations(group.Key, ct)).Select(activation => activation.Key).ToHashSet(StringComparer.Ordinal);
            active.AddRange(group.Where(record => live.Contains(record.Key)));
        }

        return [.. active];
    }
}
