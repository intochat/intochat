using IntoChat.OS.Modules.LocalDisk;
using DigitalBrain.Capabilities.Storage.Confinement;

namespace IntoChat.OS.Modules.Apps;

// Registers <Home>/apps on the brain's local disk so Files can list the dropped folders.
// LocalDisk names a root by its folder, so the item title is the last segment (apps). The
// capability has one flag set for every root, so this root is not separately read-only.
public static class AppsDirectoryRoot
{
    public const string Label = "Apps";

    public static string Prepare(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = PathConfinement.Normalize(path);
        Directory.CreateDirectory(full);
        PathConfinement.RequireNoLinks(full);
        _ = PathConfinement.Resolve([full], full);
        return full;
    }

    public static async Task Allow(ILocalDisk disk, string path)
    {
        ArgumentNullException.ThrowIfNull(disk);
        await disk.AllowRoot(Prepare(path)).ConfigureAwait(false);
    }
}
