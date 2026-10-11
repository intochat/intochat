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
    // Operations that deploy behaviors hand a whole program to the runtime, which can outlast the default call timeout.
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> WriteBehavior(string path, string source, Guid operationId);
    Task<AppSnapshot> WriteFile(string path, string source, Guid operationId); // patch bump
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections);
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> Rollback(string version);
    Task<AppSnapshot> BeginBuild(string? instruction);
    Task<Guid> CurrentBuildAttempt();
    // Publishes AppActivity (State is one of AppActivity.States) and, while building, appends Detail to the activity feed.
    Task Progress(string state, string detail);
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error, AppManifest? manifest = null);
    Task<AppBundle> Export();
    // Publishes the current version once its scripts compile: it becomes the Marketplace listing and the
    // bundle every Install copies, until the next Publish.
    Task<AppSnapshot> Publish();
    // The published version's bundle: a published app is public by definition, anyone may copy it.
    [Public] Task<AppBundle> ExportPublished();
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
    // Development only, for a copy of a feature: writes this program back into its owner's
    // features folder as the next version, then re-attaches the copy to it.
    [ResponseTimeout("00:10:00")] Task<AppSnapshot> SaveToSource();
    [ResponseTimeout("00:10:00")] Task Delete();
    // Publishes AppRequested for a synapse to react to. Nothing waits for it: the answer is neuron state.
    Task<Guid> Request(string operation, string input);
    // The fast path: one sentence becomes one synapse of this app, written against the contracts of the focus
    // neurons (keys of what the person is looking at). Records the request in Synapses and returns at once;
    // the outcome lands there too, with SynapseWritten or SynapseRejected. A replayed operation id does nothing.
    Task<AppSnapshot> WriteSynapse(string sentence, string[] focus, Guid operationId);
    // The fast-path writer's deploy, outside this neuron's turn: StageSynapse puts the requested synapse's file
    // into the app (files first) and answers the program to deploy; the writer deploys it and CompleteSynapse
    // lands the outcome: no errors means written, errors restore what the staged path held. Program answers what
    // the app runs now. Module code only; no edge caller stages or writes an outcome.
    [Internal] Task<AppProgram> StageSynapse(Guid operationId, string path, string source);
    [Internal] Task<AppProgram> Program();
    [Internal] Task<AppSnapshot> CompleteSynapse(Guid operationId, string[] errors, long elapsedMs, int attempts, string? baseSynapse = null, string? hash = null);

    static string Key(string brainId, string name) => $"{brainId}/apps/{name}";

    // The app's name when an instance key is one of this brain's apps; brain.Kernel.Instances<IApp>() lists them.
    static string? NameOf(string brainId, string key)
    {
        var prefix = brainId + "/apps/";
        return key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length && key.IndexOf('/', prefix.Length) < 0 ? key[prefix.Length..] : null;
    }
}
