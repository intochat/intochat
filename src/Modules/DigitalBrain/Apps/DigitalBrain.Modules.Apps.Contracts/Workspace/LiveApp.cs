namespace DigitalBrain.Apps;

// A live app is created running in its brain — no package, no gate. It is edited per
// behavior, its scenario document is canonical, and it is allowed to be red: verdicts and
// behavior errors are state to revisit, never a reason the app cannot exist.
[GenerateSerializer, Alias("apps.create-app")]
public sealed record CreateApp(
    [property: Id(0)] Guid OperationId,
    [property: Id(1)] string Title,
    [property: Id(2)] string Description,
    [property: Id(3)] string Runtime);

// Everything the Creator edits: the manifest view, the files, the canonical scenario
// document, the latest per-scenario verdicts and any per-behavior deploy failures.
[GenerateSerializer, Alias("apps.app-content")]
public sealed record AppContent(
    [property: Id(0)] PackageManifest Manifest,
    [property: Id(1)] Dictionary<string, string> Files,
    [property: Id(2)] AppAuthoringDocument? Document,
    [property: Id(3)] AppScenarioVerdict[] Verdicts,
    [property: Id(4)] Dictionary<string, string> BehaviorErrors);
