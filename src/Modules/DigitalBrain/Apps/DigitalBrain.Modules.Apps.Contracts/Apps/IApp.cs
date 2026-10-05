using Orleans.Metadata;

namespace DigitalBrain.Apps;

// The app: manifest, spec, files, connections,
// semver history, status and activity — the only durable thing in the Apps module.
[Alias("apps.app2"), DefaultGrainType("apps.app2")]
public interface IApp : INeuron
{
    [Orleans.Concurrency.ReadOnly, Orleans.Concurrency.AlwaysInterleave] Task<AppSnapshot> Read();
    Task<AppSnapshot> Create(AppManifest manifest, string spec);
    Task<AppSnapshot> SaveSpec(string spec, Guid operationId);                 // minor bump
    Task<AppSnapshot> SaveManifest(AppManifest manifest, Guid operationId);    // patch bump
    // Operations that deploy behaviors hot-swap ICSharpFiles, which can outlast the default call timeout.
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> WriteBehavior(string path, string source, Guid operationId);
    Task<AppSnapshot> WriteFile(string path, string source, Guid operationId); // patch bump
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections);
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> Rollback(string version);
    Task<AppSnapshot> BeginBuild(string? instruction);
    Task<Guid> CurrentBuildAttempt();
    Task Progress(string line);
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error, AppManifest? manifest = null);
    Task<AppSnapshot> BeginVerify();
    // Verdicts bind to the version that was actually tested; a run finishing after an edit
    // marks that older version, never the current one.
    Task RecordVerdicts(AppScenarioVerdict[] verdicts, bool green, string? version = null);
    Task<AppBundle> Export();
    // Publishes the current green version: it becomes the Marketplace listing and the bundle
    // every Install copies, until the next Publish.
    Task<AppSnapshot> Publish();
    // The published version's bundle, readable by anyone: a published app is public by definition.
    Task<AppBundle> ExportPublished();
    // A version this app still holds in its history, for development sync to prove a folder is
    // unchanged since that version; null once the version has aged out.
    [Orleans.Concurrency.ReadOnly] Task<AppBundle?> ExportVersion(string version);
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> Import(AppBundle bundle);
    // An update: the bundle's program replaces this one in place. Settings, connections and the
    // app's stored data stay; behaviors the bundle lacks stop.
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> Replace(AppBundle bundle);
    // Install records where the copy came from; recording it again (after an update) also
    // clears Edited, because the program is the publisher's again.
    Task RecordOrigin(AppOrigin origin);
    [ResponseTimeout("00:10:00")] Task Delete();
    Task<AppInvocation> Invoke(InvokeApp request);
    Task<AppInvocation> Respond(AppResponse response);
    Task<AppInvocation> ReadInvocation(Guid invocationId);
    Task<IReadOnlyList<AppInvocation>> Pending();

    static string Key(string brainId, string name) => $"{brainId}/apps/{name}";
}
