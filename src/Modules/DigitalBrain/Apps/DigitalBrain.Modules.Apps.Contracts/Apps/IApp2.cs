using Orleans.Metadata;

namespace DigitalBrain.Apps;

// Renamed to IApp in the deletion sweep. The app: manifest, spec, files, connections,
// semver history, status and activity — the only durable thing in the Apps module.
[Alias("apps.app2"), DefaultGrainType("apps.app2")]
public interface IAppV2 : INeuron
{
    [Orleans.Concurrency.ReadOnly, Orleans.Concurrency.AlwaysInterleave] Task<AppSnapshotV2> Read();
    Task<AppSnapshotV2> Create(AppManifest manifest, string spec);
    Task<AppSnapshotV2> SaveSpec(string spec, Guid operationId);                 // minor bump
    Task<AppSnapshotV2> SaveManifest(AppManifest manifest, Guid operationId);    // patch bump
    // Hot-swaps the behavior's ICSharpFile, which can outlast the default call timeout.
    [ResponseTimeout("00:10:00")] Task<AppSnapshotV2> WriteBehavior(string path, string source, Guid operationId);
    Task<AppSnapshotV2> WriteFile(string path, string source, Guid operationId); // patch bump
    Task<AppSnapshotV2> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections);
    Task<AppSnapshotV2> Rollback(string version);
    Task<AppSnapshotV2> BeginBuild(string? instruction);
    Task<Guid> CurrentBuildAttempt();
    Task Progress(string line);
    [ResponseTimeout("00:10:00")] Task<AppSnapshotV2> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error);
    Task<AppSnapshotV2> BeginVerify();
    Task RecordVerdicts(AppScenarioVerdict[] verdicts, bool green);
    Task<AppBundle> Export();
    Task<AppSnapshotV2> Import(AppBundle bundle);
    [ResponseTimeout("00:10:00")] Task Delete();
    Task<AppInvocation> Invoke(InvokeApp request);
    Task<AppInvocation> Respond(AppResponse response);
    Task<AppInvocation> ReadInvocation(Guid invocationId);
    Task<IReadOnlyList<AppInvocation>> Pending();

    static string Key(string brainId, string name) => $"{brainId}/apps/{name}";
}
