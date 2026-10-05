using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Apps;

internal sealed class AppsInventoryProvider(IDigitalBrain brain, IActivationCensus census) : IRegistryInventoryProvider
{
    public string Domain => "apps";
    public async Task<NeuronRecord[]> Persisted(string? typeId, CancellationToken ct)
    {
        var scope = BrainScope.CurrentId();
        if (typeId is not (null or "apps.app" or "microsoft.csharp.file")) { return []; }
        var records = new List<NeuronRecord>();
        var packages = await brain.Get<IApps>(scope).List().WaitAsync(ct);
        foreach (var package in packages)
        {
            records.AddRange(await brain.Get<IAppInventoryView>(scope + "/packages/" + package).Inventory().WaitAsync(ct));
        }
        return records.Where(record => typeId is null || record.TypeId == typeId).ToArray();
    }

    public async Task<NeuronRecord[]> Active(string? typeId, CancellationToken ct)
    {
        _ = BrainScope.CurrentId();
        var live = new List<ActivationRecord>();
        foreach (var type in new[] { "apps.app", "microsoft.csharp.file" }.Where(type => typeId is null || type == typeId))
        { live.AddRange(await census.Activations(type, ct)); }
        var identities = live.ToHashSet();
        return (await Persisted(typeId, ct)).Where(record => identities.Contains(new(record.TypeId, record.Key))).ToArray();
    }
}

// Host-only read of the App's existing membership; no backfill or lifecycle writes.
public interface IAppInventoryView : Orleans.IGrainWithStringKey
{
    Task<NeuronRecord[]> Inventory();
}
