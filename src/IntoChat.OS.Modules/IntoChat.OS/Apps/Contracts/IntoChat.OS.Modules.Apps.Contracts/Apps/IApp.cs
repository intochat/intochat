using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Apps;

// The app: manifest, spec, files, connections,
// semver history, status and activity — the only durable thing in the Apps module.
// A plain neuron (06-08); the alias and type stay "apps.app2". Its state took the clean break (R4-24): apps are installed again.
[DigitalBrain.Contracts.NeuronAlias("apps.app2"), DigitalBrain.Contracts.DefaultNeuronType("apps.app2")]
public interface IApp
{
    [DigitalBrain.ReadOnlyCall, DigitalBrain.Interleaves] Task<AppSnapshot> Read();
    Task<AppSnapshot> SaveRuntimeState(string stateJson, long expectedRevision);
    [DigitalBrain.Interleaves] Task<AppSnapshot> Present(long expectedProgramRevision, Presentation presentation);
    [DigitalBrain.Interleaves] Task<AppSnapshot> FailPresentation(long expectedProgramRevision, string error);
    [DigitalBrain.Interleaves] Task<AppSnapshot> WriteBlackboard(long expectedProgramRevision, Dictionary<string, string> values);
    [Internal] Task Stamp(string key, string value);
    Task<AppSnapshot> Create(AppManifest manifest, string spec);
    Task<AppSnapshot> SaveSpec(string spec, Guid operationId);                 // minor bump
    Task<AppSnapshot> SaveManifest(AppManifest manifest, Guid operationId);    // patch bump
    Task<AppSnapshot> WriteFile(string path, string source, Guid operationId); // patch bump
    [DigitalBrain.CallTimeout(600_000)] Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections);
    [DigitalBrain.CallTimeout(600_000)] Task<AppSnapshot> Rollback(string version);
    Task<AppSnapshot> BeginBuild(string? instruction);
    Task<Guid> CurrentBuildAttempt();
    // The running attempt's build flow (an IFlows id), named by BeginBuild before the flow starts; its status is read through IFlows.Read.
    [DigitalBrain.ReadOnlyCall] Task<string?> BuildFlow();
    // Ends a build whose synapse unit deployed, by its install: Installed, Updated or Unchanged goes Live with the unit's hash on the
    // activity feed; Pending stays Building with "waiting for approval: {sentence}" until a later call reports it installed.
    Task<AppSnapshot> CompleteUnitBuild(Guid attempt, string hash, DigitalBrain.InstallStatus status, string sentence);
    // Publishes AppActivity (State is one of AppActivity.States).
    Task Progress(string state, string detail);
    // The same activity write, plus declared blackboard values, in one presentation revision.
    Task Progress(string state, string detail, Dictionary<string, string> blackboard);
    [DigitalBrain.CallTimeout(600_000)] Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error, AppManifest? manifest = null);
    Task<AppBundle> Export();
    // Publishes the current version once its scripts compile: it becomes the Marketplace listing and the
    // bundle every Install copies, until the next Publish.
    Task<AppSnapshot> Publish();
    // The published version's bundle: a published app is public by definition, anyone may copy it.
    [Public] Task<AppBundle> ExportPublished();
    [Public, DigitalBrain.ReadOnlyCall] Task<AppPublication[]> PublishedReleases();
    [Public, DigitalBrain.ReadOnlyCall] Task<AppBundle> ExportRelease(string version, string digest);
    // A version this app still holds in its history, for development sync to prove a folder is
    // unchanged since that version; null once the version has aged out.
    [DigitalBrain.ReadOnlyCall] Task<AppBundle?> ExportVersion(string version);
    // The bundle and its origin land in one write before the runtime is asked, so a deploy that
    // fails or outlasts the caller leaves the copy Deploying or Failed, never without its origin.
    [DigitalBrain.CallTimeout(600_000)] Task<AppSnapshot> Import(AppBundle bundle, AppOrigin? origin = null, AppInstallReceipt? installation = null);
    // An update: the bundle's program replaces this one in place. Settings, connections and the
    // app's stored data stay; behaviors the bundle lacks stop. A
    // copy not yet Live is deployed again even at the same version.
    [DigitalBrain.CallTimeout(600_000)] Task<AppSnapshot> Replace(AppBundle bundle, AppOrigin? origin = null);
    // Install records where the copy came from; recording it again (after an update) also
    // clears Edited, because the program is the publisher's again.
    Task RecordOrigin(AppOrigin origin);
    // Development only, for a copy of a feature: writes this program back into its owner's
    // features folder as the next version, then re-attaches the copy to it.
    [DigitalBrain.CallTimeout(600_000)] Task<AppSnapshot> SaveToSource();
    [DigitalBrain.CallTimeout(600_000)] Task Delete();
    // Publishes AppRequested for a synapse to react to. Nothing waits for it: the answer is neuron state.
    Task<Guid> Request(string operation, string input);
    // An app view's own Emit, resolved by its view neuron and checked against this app by the edge: exactly Request(signal,
    // args JSON), so the signal must be a declared operation and every request rule applies.
    Task<Guid> Emit(string signal, string args);

    static string Key(string brainId, string name) => $"{brainId}/apps/{name}";

    // The app's name when an instance key is one of this brain's apps; brain.Kernel.Instances<IApp>() lists them.
    static string? NameOf(string brainId, string key)
    {
        var prefix = brainId + "/apps/";
        return key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length && key.IndexOf('/', prefix.Length) < 0 ? key[prefix.Length..] : null;
    }
}
