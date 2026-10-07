namespace DigitalBrain.Microsoft.CSharp;

[GenerateSerializer, Alias("csharp.description")]
public sealed record CSharpDescription(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Purpose,
    [property: Id(3)] DateTimeOffset CreatedAt);

// Validates ids and descriptions before the catalog neuron sees them; the ICSharpFile neuron owns source and execution.
public sealed class CSharpCatalogStore(IDigitalBrain brain)
{
    // The same rule that keys the file neuron, so a description can never exist for an id no file could have.
    private static void ValidateFileId(string scope, string id) => _ = CSharpFileKeys.For(scope, id);

    public Task<CSharpDescription> Describe(string scope, string id, string? name, string? purpose, CancellationToken ct)
    {
        ValidateFileId(scope, id);
        if (name is { Length: > 120 } || purpose is { Length: > 4000 })
        { throw new ArgumentException("A name is at most 120 characters and a purpose at most 4000."); }
        return Catalog(scope).Describe(id, name, purpose).WaitAsync(ct);
    }

    public Task<CSharpDescription[]> List(string scope, CancellationToken ct) => Catalog(scope).List().WaitAsync(ct);

    public Task<CSharpDescription> Read(string scope, string id, CancellationToken ct) => Catalog(scope).Read(id).WaitAsync(ct);

    public Task Remove(string scope, string id, CancellationToken ct) => Catalog(scope).Remove(id).WaitAsync(ct);

    private ICSharpCatalog Catalog(string scope) => brain.Get<ICSharpCatalog>(ICSharpCatalog.Key(scope));
}
