using DigitalBrain.Access;
using DigitalBrain.Units;

namespace IntoChat.OS.Modules.Apps;

// The app: manifest, spec, files, connections,
// semver history, status and activity — the only durable thing in the Apps module.
// A plain neuron (06-08); the alias and type stay "apps.app2". Its state took the clean break (R4-24): apps are installed again.
[DigitalBrain.Contracts.NeuronAlias("apps.app2"), DigitalBrain.Contracts.DefaultNeuronType("apps.app2")]
public interface IApp
{
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves] Task<AppSnapshot> Read();
    [DigitalBrain.Contracts.Interleaves] Task<AppSnapshot> Present(long expectedProgramRevision, Presentation presentation);
    Task<AppSnapshot> Create(AppManifest manifest, string spec);
    Task<AppSnapshot> SaveSpec(string spec, Guid operationId);                 // minor bump
    Task<AppSnapshot> SaveManifest(AppManifest manifest, Guid operationId);    // patch bump
    [DigitalBrain.Contracts.CallTimeout(600_000)] Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections);
    [DigitalBrain.Contracts.CallTimeout(600_000)] Task<AppSnapshot> Rollback(string version);
    Task<AppSnapshot> BeginBuild(string? instruction);
    Task<Guid> CurrentBuildAttempt();
    // The running attempt's build flow (an IFlows id), named by BeginBuild before the flow starts; its status is read through IFlows.Read.
    [DigitalBrain.Contracts.ReadOnlyCall] Task<string?> BuildFlow();
    // Ends a build whose synapse unit deployed, by its install: Installed, Updated or Unchanged goes Live with the unit's hash on the
    // activity feed; Pending stays Building with "waiting for approval: {sentence}" until a later call reports it installed.
    Task<AppSnapshot> CompleteUnitBuild(Guid attempt, string hash, DigitalBrain.Units.InstallStatus status, string sentence);
    // Publishes AppActivity (State is one of AppActivity.States).
    Task Progress(string state, string detail);
    [DigitalBrain.Contracts.CallTimeout(600_000)] Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error, AppManifest? manifest = null);
    Task<AppBundle> Export();
    // Publishes the current version once its scripts compile: it becomes the Marketplace listing and the
    // bundle every Install copies, until the next Publish.
    Task<AppSnapshot> Publish();
    // The published version's bundle: a published app is public by definition, anyone may copy it.
    [Public] Task<AppBundle> ExportPublished();
    [Public, DigitalBrain.Contracts.ReadOnlyCall] Task<AppPublication[]> PublishedReleases();
    [Public, DigitalBrain.Contracts.ReadOnlyCall] Task<AppBundle> ExportRelease(string version, string digest);
    // The bundle and its origin land in one write before the runtime is asked, so a deploy that
    // fails or outlasts the caller leaves the copy Deploying or Failed, never without its origin.
    [DigitalBrain.Contracts.CallTimeout(600_000)] Task<AppSnapshot> Import(AppBundle bundle, AppOrigin? origin = null, AppInstallReceipt? installation = null);
    // An update: the bundle's program replaces this one in place. Settings, connections and the
    // app's stored data stay; behaviors the bundle lacks stop. A
    // copy not yet Live is deployed again even at the same version.
    [DigitalBrain.Contracts.CallTimeout(600_000)] Task<AppSnapshot> Replace(AppBundle bundle, AppOrigin? origin = null);
    [DigitalBrain.Contracts.CallTimeout(600_000)] Task Delete();
    // Publishes AppRequested for a synapse to react to. Nothing waits for it: the answer is neuron state.
    Task<Guid> Request(string operation, string input);
    // An app view's own Emit, resolved by its view neuron and checked against this app by the edge: exactly Request(signal,
    // args JSON), so the signal must be a declared operation and every request rule applies.
    Task<Guid> Emit(string signal, string args);

    static string Key(string brainId, string name) => $"{brainId}/apps/{name}";

    // The app's name when an instance key is one of this brain's apps; brain.Kernel.Instances<IApp>() lists them.
    static string? NameOf(string brainId, string key)
    {
        if (!DigitalBrain.Identity.CompoundGrainKey.TryParse(key, out var compound) ||
            !string.Equals(compound.Brain.Value, brainId, StringComparison.Ordinal) ||
            !string.Equals(compound.Type, "apps", StringComparison.Ordinal))
        {
            return null;
        }

        return compound.Key.Contains('/', StringComparison.Ordinal) ||
               compound.Key.Contains('\\', StringComparison.Ordinal) ||
               compound.Key.Contains("..", StringComparison.Ordinal)
            ? null
            : compound.Key;
    }
}
