using DigitalBrain;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Desktop;

// An app neuron that records Request calls by key; every other operation is unsupported.
[Neuron("apps.app2")]
internal sealed class RecordingApp : Neuron, IApp
{
    public static List<(string Key, string Operation, string Input)> Requests { get; } = [];

    public Task<Guid> Emit(string signal, string args) => Request(signal, args);

    public Task<Guid> Request(string operation, string input)
    {
        Requests.Add((Id.Key, operation, input));
        return Task.FromResult(Guid.NewGuid());
    }

        public Task<AppSnapshot> Read() => throw new NotSupportedException();
        public Task<AppSnapshot> SaveRuntimeState(string stateJson, long expectedRevision) => throw new NotSupportedException();
        public Task<AppSnapshot> Present(long expectedProgramRevision, Presentation presentation) => throw new NotSupportedException();
        public Task<AppSnapshot> FailPresentation(long expectedProgramRevision, string error) => throw new NotSupportedException();
        public Task<AppSnapshot> WriteBlackboard(long expectedProgramRevision, Dictionary<string, string> values) => throw new NotSupportedException();
        public Task Stamp(string key, string value) => throw new NotSupportedException();
        public Task<AppSnapshot> Create(AppManifest manifest, string spec) => throw new NotSupportedException();
        public Task<AppSnapshot> SaveSpec(string spec, Guid operationId) => throw new NotSupportedException();
        public Task<AppSnapshot> SaveManifest(AppManifest manifest, Guid operationId) => throw new NotSupportedException();
        public Task<AppSnapshot> WriteBehavior(string path, string source, Guid operationId) => throw new NotSupportedException();
        public Task<AppSnapshot> WriteFile(string path, string source, Guid operationId) => throw new NotSupportedException();
        public Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections) => throw new NotSupportedException();
        public Task<AppSnapshot> Rollback(string version) => throw new NotSupportedException();
        public Task<AppSnapshot> BeginBuild(string? instruction) => throw new NotSupportedException();
        public Task<Guid> CurrentBuildAttempt() => throw new NotSupportedException();
        public Task<string?> BuildFlow() => throw new NotSupportedException();
        public Task<AppSnapshot> CompleteHeadlessBuild(Guid attempt, string hash) => throw new NotSupportedException();
        public Task Progress(string state, string detail) => throw new NotSupportedException();
        public Task Progress(string state, string detail, Dictionary<string, string> blackboard) => throw new NotSupportedException();
        public Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error, AppManifest? manifest = null) => throw new NotSupportedException();
        public Task<AppBundle> Export() => throw new NotSupportedException();
        public Task<AppSnapshot> Publish() => throw new NotSupportedException();
        public Task<AppBundle> ExportPublished() => throw new NotSupportedException();
        public Task<AppPublication[]> PublishedReleases() => throw new NotSupportedException();
        public Task<AppBundle> ExportRelease(string version, string digest) => throw new NotSupportedException();
        public Task<AppBundle?> ExportVersion(string version) => throw new NotSupportedException();
        public Task<AppSnapshot> Import(AppBundle bundle, AppOrigin? origin = null, AppInstallReceipt? installation = null) => throw new NotSupportedException();
        public Task<AppSnapshot> Replace(AppBundle bundle, AppOrigin? origin = null) => throw new NotSupportedException();
        public Task RecordOrigin(AppOrigin origin) => throw new NotSupportedException();
        public Task<AppSnapshot> SaveToSource() => throw new NotSupportedException();
        public Task Delete() => throw new NotSupportedException();
        public Task<AppSnapshot> WriteSynapse(string sentence, string[] focus, Guid operationId) => throw new NotSupportedException();
        public Task<AppSnapshot> CompleteSynapse(Guid operationId, string[] errors, long elapsedMs, int attempts, string? baseSynapse = null, string? hash = null) => throw new NotSupportedException();
}
