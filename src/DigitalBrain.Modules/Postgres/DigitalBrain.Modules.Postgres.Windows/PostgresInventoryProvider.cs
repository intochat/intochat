using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Postgres.Windows;

// The existing resource service enumerates installed app ownership, not database tables.
// Tables owned only by unindexed legacy/direct callers are outside this view.
internal sealed class PostgresInventoryProvider(PostgresAppResources resources, IActivationCensus census) : IRegistryInventoryProvider
{
    private const string Type = "postgres.table";
    public string Domain => "postgres";
    public async Task<NeuronRecord[]> Persisted(string? typeId, CancellationToken ct)
    {
        _ = BrainScope.CurrentId();
        if (typeId is not null && typeId != Type) { return []; }
        var result = await resources.List(ct);
        if (result.Errors.Length > 0) { throw new InvalidOperationException("Some app table ownership could not be read."); }
        return result.Resources.Select(resource => new NeuronRecord(Domain, Type, resource.TableId, resource.TableId)).ToArray();
    }
    public async Task<NeuronRecord[]> Active(string? typeId, CancellationToken ct)
    {
        _ = BrainScope.CurrentId();
        if (typeId is not null && typeId != Type) { return []; }
        var live = (await census.Activations(Type, ct)).Select(record => record.Key).ToHashSet(StringComparer.Ordinal);
        return (await Persisted(typeId, ct)).Where(record => live.Contains(record.Key)).ToArray();
    }
}
