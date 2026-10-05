namespace DigitalBrain.Apps;

// Renamed to AppStatus in the deletion sweep, when the Workspace enum goes.
public enum AppStatusV2 { Drafted, Building, Live, Failed }

[GenerateSerializer, Alias("apps.version")]
public sealed record AppVersionSummary(
    [property: Id(0)] string Version,
    [property: Id(1)] DateTimeOffset SavedAt,
    [property: Id(2)] bool? Green);

// Renamed to AppSnapshot in the deletion sweep.
[GenerateSerializer, Alias("apps.snapshot2")]
public sealed record AppSnapshotV2(
    [property: Id(0)] string Name,
    [property: Id(1)] AppManifest Manifest,
    [property: Id(2)] string Spec,
    [property: Id(3)] Dictionary<string, string> Files,
    [property: Id(4)] Dictionary<string, string> Settings,
    [property: Id(5)] Dictionary<string, string> Connections,
    [property: Id(6)] string Version,
    [property: Id(7)] AppStatusV2 Status,
    [property: Id(8)] string[] Activity,
    [property: Id(9)] AppVersionSummary[] Versions,
    [property: Id(10)] AppScenarioVerdict[] Verdicts);
