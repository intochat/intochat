using System.Security.Cryptography;
using System.Text.Json;

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
    public static string FileKey(string scope, string id)
    {
        ValidateFileId(scope, id);
        return "csharp-" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { scope, id })));
    }

    private static void ValidateFileId(string scope, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        { throw new ArgumentException("Use a C# file ID containing 1–100 ASCII letters, digits, hyphens or underscores.", nameof(id)); }
    }

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
