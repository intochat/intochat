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
    // Hot-swaps the behavior's ICSharpFile, which can outlast the default call timeout.
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> WriteBehavior(string path, string source, Guid operationId);
    Task<AppSnapshot> WriteFile(string path, string source, Guid operationId); // patch bump
    Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections);
    Task<AppSnapshot> Rollback(string version);
    Task<AppSnapshot> BeginBuild(string? instruction);
    Task<Guid> CurrentBuildAttempt();
    Task Progress(string line);
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error);
    Task<AppSnapshot> BeginVerify();
    Task RecordVerdicts(AppScenarioVerdict[] verdicts, bool green);
    Task<AppBundle> Export();
    Task<AppSnapshot> Import(AppBundle bundle);
    [ResponseTimeout("00:10:00")] Task Delete();
    Task<AppInvocation> Invoke(InvokeApp request);
    Task<AppInvocation> Respond(AppResponse response);
    Task<AppInvocation> ReadInvocation(Guid invocationId);
    Task<IReadOnlyList<AppInvocation>> Pending();

    static string Key(string brainId, string name) => $"{brainId}/apps/{name}";
}
