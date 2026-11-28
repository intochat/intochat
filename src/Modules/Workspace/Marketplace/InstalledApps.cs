using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Runtime;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Apps;

// The apps a brain holds, by name, and whether a listed copy has a newer version: what GET /apps
// shows and what the app store groups.
internal static class InstalledApps
{
    public static Task<IReadOnlyList<(string Name, AppSnapshot Snapshot)>> Read(INeuronProxies brain, IBrainObserver kernel, string scope, CancellationToken ct)
        => Read(brain, kernel, scope, ct, skipNames: null);

    public static async Task<IReadOnlyList<(string Name, AppSnapshot Snapshot)>> Read(
        INeuronProxies brain,
        IBrainObserver kernel,
        string scope,
        CancellationToken ct,
        IReadOnlySet<string>? skipNames)
    {
        var brainId = new BrainId(scope);
        List<string> names = [];
        await foreach (var instance in kernel.Instances<IApp>(ct))
        {
            var candidateKey = NeuronId.TryParse(instance.Key, out var parsedId) ? parsedId.FullKey() : instance.Key;
            if (IntoChat.OS.Host.Primitives.AppRef.TryParse(brainId, candidateKey, out var app)
                && (skipNames is null || !skipNames.Contains(app.Name)))
            {
                names.Add(app.Name);
            }
        }
        names.Sort(StringComparer.Ordinal);
        if (names.Count == 0)
        {
            return [];
        }

        var reads = names.Select(async name =>
            (name, await brain.Get<IApp>(IntoChat.OS.Host.Primitives.AppRef.Of(brainId, name).Key).Read().WaitAsync(ct)));
        return await Task.WhenAll(reads);
    }

    // The same rule as AppInstaller.Entry: an unedited copy whose publisher listed a newer version.
    public static bool UpdateAvailable(AppSnapshot snapshot, IReadOnlyList<MarketplaceListing> listings)
        => CatalogEntry.IsUpdateAvailable(snapshot, listings);
}
