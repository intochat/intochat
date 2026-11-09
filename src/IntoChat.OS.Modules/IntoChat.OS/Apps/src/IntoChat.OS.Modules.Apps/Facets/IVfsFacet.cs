using Orleans;
using Orleans.CodeGeneration;

namespace IntoChat.OS.Modules.Apps.Facets;

[Alias("apps.vfs-facet")]
[Version(1)]
public interface IVfsFacet : IGrainExtension
{
    Task<string[]> ListFilesAsync(string path);
    Task<string?> ReadTextAsync(string path);
    Task WriteTextAsync(string path, string content);
    Task<bool> ExistsAsync(string path);
}
