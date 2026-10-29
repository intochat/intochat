
namespace IntoChat.OS.Modules.Apps;

// One gallery card: which group it sits in ("builtin" | "mine" | "others"), what it is
// ("app" | "connection"), and whether this brain already has it (a connection: is connected).
// Available is false for a connection this host has not configured: it shows, but cannot connect.
public sealed record GalleryEntry(
    string Group,
    string Publisher,
    string Name,
    string Title,
    string Description,
    string? Icon,
    bool Installed,
    bool UpdateAvailable,
    string? InstalledName,
    string Kind = "app",
    string? ConnectorId = null,
    bool Available = true);

public enum InstallStatus { Ok = 0, Refused = 1, NeedsConsent = 2 }

// What an install, update or remove did: the installed app's name, or why nothing changed. NeedsConsent carries the
// requirements the person must see and accept (all of them for an install, the added ones for an update); Latest is
// everything the release asks for, the panel's full list.
public sealed record InstallOutcome(
    bool Ok,
    string? InstalledName,
    string? Message,
    InstallStatus Status = InstallStatus.Ok,
    AppRequirements? Requires = null,
    AppRequirements? Latest = null)
{
    public static InstallOutcome Installed(string name) => new(true, name, null);
    public static InstallOutcome Refused(string message) => new(false, null, message, InstallStatus.Refused);
    public static InstallOutcome NeedsConsent(AppRequirements requires, AppRequirements? latest = null)
        => new(false, null, "Review what this app asks for, then install again.", InstallStatus.NeedsConsent, requires, latest ?? requires);
}

// A listed app: both Version and Digest name one exact release; neither names the latest listing.
public sealed record AppRef(
    string Publisher,
    string App,
    string? Version = null,
    string? Digest = null)
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
public sealed record InstallRequest(
    AppRef Ref,
    string Consent,
    Guid OperationId,
    string? Name = null,
    Dictionary<string, string>? Bindings = null);

public sealed record UpdateRequest(string Name, string? Consent = null);

// One app as the Apps detail shows it. Status is "available" | "installed" | "update" | "unavailable".
public sealed record AppDetail(
    string Publisher,
    string App,
    string Title,
    string Description,
    string? Icon,
    string? LatestVersion,
    string? InstalledName,
    string? InstalledVersion,
    bool Edited,
    AppRequirements Requires,
    AppRequirements? Consented,
    AppSlot[] Slots,
    string Status,
    string? Reason);

// The Marketplace as a brain's scripts see it: the gallery, grouped, and one consented install path. Get stays for
// scripts and installs only apps that ask for nothing; an app with requirements is installed from Apps.
// A plain neuron (06-08).
[DigitalBrain.Contracts.NeuronAlias("apps.store"), DigitalBrain.Contracts.DefaultNeuronType("apps.store")]
public interface IAppStore
{
    [DigitalBrain.ReadOnlyCall, DigitalBrain.Interleaves] Task<IReadOnlyList<GalleryEntry>> List();
    Task<InstallOutcome> Get(string publisher, string name);
    Task<InstallOutcome> Install(InstallRequest request);
    Task<InstallOutcome> Update(UpdateRequest request);
    Task<InstallOutcome> Remove(string name);
    [DigitalBrain.ReadOnlyCall, DigitalBrain.Interleaves] Task<AppDetail?> Describe(AppRef reference);
    static string Key(string brainId) => $"{brainId}/{Relative}";
    // The id inside its brain.
    const string Relative = "apps/store";
}
