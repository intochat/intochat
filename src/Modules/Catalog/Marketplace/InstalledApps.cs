using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Runtime;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Apps;

// The apps a brain holds, by name, and whether a listed copy has a newer version: what GET /apps
// shows and what the app store groups.
internal static class InstalledApps
{
    public static async Task<IReadOnlyList<(string Name, AppSnapshot Snapshot)>> Read(INeuronProxies brain, IBrainObserver kernel, string scope, CancellationToken ct)
    {
        var brainId = new BrainId(scope);
        List<string> names = [];
        await foreach (var instance in kernel.Instances<IApp>(ct)) { if (IntoChat.OS.Host.Primitives.AppRef.TryParse(brainId, instance.Key, out var app)) { names.Add(app.Name); } }
        names.Sort(StringComparer.Ordinal);
        var snapshots = new List<(string, AppSnapshot)>(names.Count);
        foreach (var name in names) { snapshots.Add((name, await brain.Get<IApp>(IntoChat.OS.Host.Primitives.AppRef.Of(brainId, name).Key).Read().WaitAsync(ct))); }
        return snapshots;
    }

    // The same rule as AppInstaller.Entry: an unedited copy whose publisher listed a newer version.
    public static bool UpdateAvailable(AppSnapshot snapshot, IReadOnlyList<MarketplaceListing> listings)
        => CatalogEntry.IsUpdateAvailable(snapshot, listings);
}
