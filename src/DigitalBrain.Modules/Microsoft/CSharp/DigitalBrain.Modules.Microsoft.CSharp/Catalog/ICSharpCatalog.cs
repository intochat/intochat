using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Catalog;

// The human-facing names of a workspace's C# files, keyed "{scope}/csharp/catalog". Lives in the
// module runtime assembly, not Contracts: the assistant's tools are its only caller.
[Alias("csharp.catalog"), DefaultGrainType("csharp.catalog")]
public interface ICSharpCatalog : INeuron
{
    Task<CSharpDescription> Describe(string id, string? name, string? purpose);
    [Orleans.Concurrency.ReadOnly] Task<CSharpDescription[]> List();
    [Orleans.Concurrency.ReadOnly] Task<CSharpDescription> Read(string id);
    Task Remove(string id);

    static string Key(string scope) => scope + "/csharp/catalog";
}

[GenerateSerializer, Alias("csharp.catalog.state")]
public sealed class CSharpCatalogState
{
    [Id(0)] public Dictionary<string, CSharpDescription> Files { get; set; } = new(StringComparer.Ordinal);
}
