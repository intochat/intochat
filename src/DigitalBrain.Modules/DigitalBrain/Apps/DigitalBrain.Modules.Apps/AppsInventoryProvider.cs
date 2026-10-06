using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Apps;

internal sealed class AppsInventoryProvider(IDigitalBrain brain, IActivationCensus census) : IRegistryInventoryProvider
{
    public string Domain => "apps";

    public async Task<NeuronRecord[]> Persisted(string? typeId, CancellationToken ct)
    {
        var scope = BrainScope.CurrentId();
        if (typeId is not (null or "apps.app2" or "microsoft.csharp.file")) { return []; }
        var records = new List<NeuronRecord>();
        foreach (var name in await brain.Get<IAppIndex>(scope).List().WaitAsync(ct))
        {
            var key = IApp.Key(scope, name);
            var app = await brain.Get<IApp>(key).Read().WaitAsync(ct);
            records.Add(new("apps", "apps.app2", key, name));
            records.AddRange(app.Files.Keys.Where(AppFiles.IsSynapse)
                .Select(path => new NeuronRecord("apps", "microsoft.csharp.file", key + "/" + path, name + " / " + path)));
        }
        return [.. records.Where(record => typeId is null || record.TypeId == typeId)];
    }

    public async Task<NeuronRecord[]> Active(string? typeId, CancellationToken ct)
    {
        _ = BrainScope.CurrentId();
        var live = new List<ActivationRecord>();
        foreach (var type in new[] { "apps.app2", "microsoft.csharp.file" }.Where(type => typeId is null || type == typeId))
        { live.AddRange(await census.Activations(type, ct)); }
        var identities = live.ToHashSet();
        return [.. (await Persisted(typeId, ct)).Where(record => identities.Contains(new(record.TypeId, record.Key)))];
    }
}
