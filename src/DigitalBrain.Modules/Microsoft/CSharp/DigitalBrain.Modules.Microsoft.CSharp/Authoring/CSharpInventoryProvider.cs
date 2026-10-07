using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Files;

namespace DigitalBrain.Microsoft.CSharp.Authoring;

// Covers standalone files described in the existing authoring catalog. Direct grain callers
// bypass that catalog; app-owned files are described by Apps, not duplicated here.
internal sealed class CSharpInventoryProvider(CSharpCatalogStore catalog, IActivationCensus census) : IRegistryInventoryProvider
{
    private const string Type = "microsoft.csharp.file";
    public string Domain => "csharp";
    public async Task<NeuronRecord[]> Persisted(string? typeId, CancellationToken ct)
    {
        var scope = BrainScope.CurrentId();
        if (typeId is not null && typeId != Type) { return []; }
        return (await catalog.List(scope, ct)).Select(file => new NeuronRecord(Domain, Type,
            CSharpFileKeys.For(scope, file.Id), file.Name)).ToArray();
    }
    public async Task<NeuronRecord[]> Active(string? typeId, CancellationToken ct)
    {
        _ = BrainScope.CurrentId();
        if (typeId is not null && typeId != Type) { return []; }
        var live = (await census.Activations(Type, ct)).Select(record => record.Key).ToHashSet(StringComparer.Ordinal);
        return (await Persisted(typeId, ct)).Where(record => live.Contains(record.Key)).ToArray();
    }
}
