using Microsoft.Extensions.Logging;

namespace DigitalBrain.Apps;

// Development sync only fast-forwards a folder: it is written when it still holds exactly what the
// brain recorded for the folder's own version. A folder with work the brain never saw (an edit
// without a version bump, an older branch's checkout) is left alone and the conflict is logged.
internal static class ShippedAppSync
{
    public static async Task WriteIfFastForward(IShippedAppSource source, IApp app, AppBundle published, ILogger logger)
    {
        var folder = source.Load().FirstOrDefault(bundle => bundle.Name == published.Name);
        if (folder is not null)
        {
            if (folder.Version == published.Version) { return; }
            if (await app.ExportVersion(folder.Version) is not { } recorded || !SameContent(folder, recorded))
            {
                logger.LogWarning("The {App} folder holds work the brain never saw at version {Version}; it is left as it is, not overwritten with {Published}.",
                    published.Name, folder.Version, published.Version);
                return;
            }
        }
        source.Write(published);
    }

    // Equal when they serialize to the same folder: the same normalized text a folder would hold.
    public static bool SameContent(AppBundle left, AppBundle right)
    {
        var a = ShippedAppFolder.ToFiles(left with { Version = right.Version });
        var b = ShippedAppFolder.ToFiles(right);
        return a.Count == b.Count && a.All(file => b.TryGetValue(file.Key, out var other) && other == file.Value);
    }
}
