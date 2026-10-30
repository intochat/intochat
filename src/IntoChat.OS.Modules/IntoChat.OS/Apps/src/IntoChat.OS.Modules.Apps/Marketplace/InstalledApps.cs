using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Spi;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Runtime;

namespace IntoChat.OS.Modules.Apps;

// The apps a brain holds, by name, and whether a listed copy has a newer version: what GET /apps
// shows and what the app store groups.
internal static class InstalledApps
{
    public static async Task<IReadOnlyList<(string Name, AppSnapshot Snapshot)>> Read(INeuronProxies brain, IBrainObserver kernel, string scope, CancellationToken ct)
    {
        List<string> names = [];
        await foreach (var instance in kernel.Instances<IApp>(ct)) { if (IApp.NameOf(scope, instance.Key) is { } app) { names.Add(app); } }
        names.Sort(StringComparer.Ordinal);
        var snapshots = new List<(string, AppSnapshot)>(names.Count);
        foreach (var name in names) { snapshots.Add((name, await brain.Get<IApp>(IApp.Key(scope, name)).Read().WaitAsync(ct))); }
        return snapshots;
    }

    // The same rule as AppInstaller.Entry: an unedited copy whose publisher listed a newer version.
    public static bool UpdateAvailable(AppSnapshot snapshot, IReadOnlyList<MarketplaceListing> listings)
        => snapshot.Origin is { } source && !snapshot.Edited
            && listings.FirstOrDefault(listing => listing.Publisher == source.Publisher && listing.Name == source.Name) is { } listed
            && AppVersions.Compare(listed.Version, source.Version) > 0;
}
