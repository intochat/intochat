using Orleans.Concurrency;
using Orleans.Metadata;

namespace IntoChat.OS.Apps;

// One gallery card: which group it sits in ("builtin" | "mine" | "others"), what it is
// ("app" | "connection"), and whether this brain already has it.
[GenerateSerializer, Alias("apps.store.entry")]
public sealed record GalleryEntry(
    [property: Id(0)] string Group,
    [property: Id(1)] string Publisher,
    [property: Id(2)] string Name,
    [property: Id(3)] string Title,
    [property: Id(4)] string Description,
    [property: Id(5)] string? Icon,
    [property: Id(6)] bool Installed,
    [property: Id(7)] bool UpdateAvailable,
    [property: Id(8)] string? InstalledName,
    [property: Id(9)] string Kind = "app",
    [property: Id(10)] string? ConnectorId = null);

// What Get did: the installed app's name, or why nothing was installed.
[GenerateSerializer, Alias("apps.store.outcome")]
public sealed record InstallOutcome(
    [property: Id(0)] bool Ok,
    [property: Id(1)] string? InstalledName,
    [property: Id(2)] string? Message);

// The Marketplace as a brain's scripts see it: the gallery, grouped, and Get to install.
[Alias("apps.store"), DefaultGrainType("apps.store")]
public interface IAppStore : INeuron
{
    [ReadOnly, AlwaysInterleave] Task<IReadOnlyList<GalleryEntry>> List();
    Task<InstallOutcome> Get(string publisher, string name);
    static string Key(string brainId) => $"{brainId}/{Relative}";
    // The id inside its brain.
    const string Relative = "apps/store";
}
