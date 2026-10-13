namespace DigitalBrain.Apps;

// Deploying: the program and its origin are held, the runtime has not confirmed it yet.
public enum AppStatus { Drafted, Building, Live, Failed, Deploying }

[GenerateSerializer, Alias("apps.version")]
public sealed record AppVersionSummary(
    [property: Id(0)] string Version,
    [property: Id(1)] DateTimeOffset SavedAt);

[GenerateSerializer, Alias("apps.snapshot2")]
public sealed record AppSnapshot(
    [property: Id(0)] string Name,
    [property: Id(1)] AppManifest Manifest,
    [property: Id(2)] string Spec,
    [property: Id(3)] Dictionary<string, string> Files,
    [property: Id(4)] Dictionary<string, string> Settings,
    [property: Id(5)] Dictionary<string, string> Connections,
    [property: Id(6)] string Version,
    [property: Id(7)] AppStatus Status,
    [property: Id(8)] string[] Activity,
    [property: Id(9)] AppVersionSummary[] Versions,
    [property: Id(10)] string? PublishedVersion = null,
    [property: Id(11)] AppOrigin? Origin = null,
    [property: Id(12)] bool Edited = false,
    [property: Id(13)] AppSynapse[]? Synapses = null,
    [property: Id(14)] AppInstallReceipt? Installation = null);
