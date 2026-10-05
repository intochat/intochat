using DigitalBrain;
using Orleans.Metadata;

namespace DigitalBrain.Apps;

// One package revision installed in one workspace. Its script runs in a container and cannot host
// neurons, so the app is the script's address in the brain: settings, operations and invocations.
[Alias("apps.app"), DefaultGrainType("apps.app")]
public interface IApp : INeuron
{
    [Orleans.Concurrency.ReadOnly, Orleans.Concurrency.AlwaysInterleave] Task<AppSnapshot> Read();
    // Each lifecycle change stops one C# file and starts another, which can outlast the default call timeout.
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> Install(InstallApp request);
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> Configure(ConfigureApp request);
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> Upgrade(UpgradeApp request);
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> Uninstall(UninstallApp request);
    // The live app: created running, edited per behavior, allowed red.
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> Create(CreateApp request);
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> WriteBehavior(string path, string source, Guid operationId);
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> DeleteBehavior(string path, Guid operationId);
    // Non-program files (app.spec.md, tests.cs, prompts): stored, never deployed.
    Task<AppSnapshot> WriteFile(string path, string source, Guid operationId);
    Task<AppSnapshot> SaveManifest(PackageManifest manifest, Guid operationId);
    Task<AppSnapshot> SaveDocument(AppAuthoringDocument document, Guid operationId);
    Task RecordVerdicts(AppScenarioVerdict[] verdicts, Guid operationId);
    [Orleans.Concurrency.ReadOnly, Orleans.Concurrency.AlwaysInterleave] Task<AppContent> ReadContent();
    Task<AppInvocation> Invoke(InvokeApp request);
    Task<AppInvocation> Respond(AppResponse response);
    Task<AppInvocation> ReadInvocation(Guid invocationId);
    Task<IReadOnlyList<AppInvocation>> Pending();
}
