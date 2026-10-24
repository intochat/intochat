using Orleans.Concurrency;
using Orleans.Metadata;

namespace IntoChat.OS.Apps;

// One gallery card: which group it sits in ("builtin" | "mine" | "others"), what it is
// ("app" | "connection"), and whether this brain already has it (a connection: is connected).
// Available is false for a connection this host has not configured: it shows, but cannot connect.
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
    [property: Id(10)] string? ConnectorId = null,
    [property: Id(11)] bool Available = true);

public enum InstallStatus { Ok = 0, Refused = 1, NeedsConsent = 2 }

// What an install, update or remove did: the installed app's name, or why nothing changed. NeedsConsent carries the
// requirements the person must see and accept (all of them for an install, the added ones for an update); Latest is
// everything the release asks for, the panel's full list.
[GenerateSerializer, Alias("apps.store.outcome")]
public sealed record InstallOutcome(
    [property: Id(0)] bool Ok,
    [property: Id(1)] string? InstalledName,
    [property: Id(2)] string? Message,
    [property: Id(3)] InstallStatus Status = InstallStatus.Ok,
    [property: Id(4)] AppRequirements? Requires = null,
    [property: Id(5)] AppRequirements? Latest = null)
{
    public static InstallOutcome Installed(string name) => new(true, name, null);
    public static InstallOutcome Refused(string message) => new(false, null, message, InstallStatus.Refused);
    public static InstallOutcome NeedsConsent(AppRequirements requires, AppRequirements? latest = null)
        => new(false, null, "Review what this app asks for, then install again.", InstallStatus.NeedsConsent, requires, latest ?? requires);
}

// A listed app: both Version and Digest name one exact release; neither names the latest listing.
[GenerateSerializer, Alias("apps.store.ref")]
public sealed record AppRef(
    [property: Id(0)] string Publisher,
    [property: Id(1)] string App,
    [property: Id(2)] string? Version = null,
    [property: Id(3)] string? Digest = null)
{
    public bool IsExact() => (Version, Digest) switch
    {
        (null, null) => false,
        (not null, not null) => true,
        _ => throw new ArgumentException("An exact release names both its version and its digest."),
    };
}

// The one install request: Consent is AppConsent.Of the requirements the person was shown; Bindings map a manifest
// slot to one of this brain's connections of the slot's connector kind.
[GenerateSerializer, Alias("apps.store.install-request")]
public sealed record InstallRequest(
    [property: Id(0)] AppRef Ref,
    [property: Id(1)] string Consent,
    [property: Id(2)] Guid OperationId,
    [property: Id(3)] string? Name = null,
    [property: Id(4)] Dictionary<string, string>? Bindings = null);

[GenerateSerializer, Alias("apps.store.update-request")]
public sealed record UpdateRequest([property: Id(0)] string Name, [property: Id(1)] string? Consent = null);

// One app as the Apps detail shows it. Status is "available" | "installed" | "update" | "unavailable".
[GenerateSerializer, Alias("apps.store.detail")]
public sealed record AppDetail(
    [property: Id(0)] string Publisher,
    [property: Id(1)] string App,
    [property: Id(2)] string Title,
    [property: Id(3)] string Description,
    [property: Id(4)] string? Icon,
    [property: Id(5)] string? LatestVersion,
    [property: Id(6)] string? InstalledName,
    [property: Id(7)] string? InstalledVersion,
    [property: Id(8)] bool Edited,
    [property: Id(9)] AppRequirements Requires,
    [property: Id(10)] AppRequirements? Consented,
    [property: Id(11)] AppSlot[] Slots,
    [property: Id(12)] string Status,
    [property: Id(13)] string? Reason);

// The Marketplace as a brain's scripts see it: the gallery, grouped, and one consented install path. Get stays for
// scripts and installs only apps that ask for nothing; an app with requirements is installed from Apps.
[Alias("apps.store"), DefaultGrainType("apps.store")]
public interface IAppStore : INeuron
{
    [ReadOnly, AlwaysInterleave] Task<IReadOnlyList<GalleryEntry>> List();
    Task<InstallOutcome> Get(string publisher, string name);
    Task<InstallOutcome> Install(InstallRequest request);
    Task<InstallOutcome> Update(UpdateRequest request);
    Task<InstallOutcome> Remove(string name);
    [ReadOnly, AlwaysInterleave] Task<AppDetail?> Describe(AppRef reference);
    static string Key(string brainId) => $"{brainId}/{Relative}";
    // The id inside its brain.
    const string Relative = "apps/store";
}
