namespace IntoChat.OS.Modules.Apps;

// Deploying: the program and its origin are held, the runtime has not confirmed it yet.
public enum AppStatus { Drafted, Building, Live, Failed, Deploying }

public sealed record AppVersionSummary(
    string Version,
    DateTimeOffset SavedAt);

public sealed record AppSnapshot(
    string Name,
    AppManifest Manifest,
    string Spec,
    Dictionary<string, string> Files,
    Dictionary<string, string> Settings,
    Dictionary<string, string> Connections,
    string Version,
    AppStatus Status,
    string[] Activity,
    AppVersionSummary[] Versions,
    string? PublishedVersion = null,
    AppOrigin? Origin = null,
    bool Edited = false,
    AppSynapse[]? Synapses = null,
    AppInstallReceipt? Installation = null,
    AppPresentation? Presentation = null,
    long ProgramRevision = 0,
    string? MigrationTarget = null,
    string? RuntimeStateJson = null,
    long RuntimeStateRevision = 0);
