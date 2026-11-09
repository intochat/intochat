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
        public Task<AppSnapshot> Present(long expectedProgramRevision, Presentation presentation) => throw new NotSupportedException();
        public Task<AppSnapshot> Create(AppManifest manifest, string spec) => throw new NotSupportedException();
        public Task<AppSnapshot> SaveSpec(string spec, Guid operationId) => throw new NotSupportedException();
        public Task<AppSnapshot> SaveManifest(AppManifest manifest, Guid operationId) => throw new NotSupportedException();
        public Task<AppSnapshot> Configure(Dictionary<string, string> settings, Dictionary<string, string> connections) => throw new NotSupportedException();
        public Task<AppSnapshot> Rollback(string version) => throw new NotSupportedException();
        public Task<AppSnapshot> BeginBuild(string? instruction) => throw new NotSupportedException();
        public Task<Guid> CurrentBuildAttempt() => throw new NotSupportedException();
        public Task<string?> BuildFlow() => throw new NotSupportedException();
        public Task<AppSnapshot> CompleteUnitBuild(Guid attempt, string hash, DigitalBrain.InstallStatus status, string sentence) => throw new NotSupportedException();
        public Task Progress(string state, string detail) => throw new NotSupportedException();
        public Task<AppSnapshot> CompleteBuild(Guid attempt, Dictionary<string, string>? files, string? error, AppManifest? manifest = null) => throw new NotSupportedException();
        public Task<AppBundle> Export() => throw new NotSupportedException();
        public Task<AppSnapshot> Publish() => throw new NotSupportedException();
        public Task<AppBundle> ExportPublished() => throw new NotSupportedException();
        public Task<AppPublication[]> PublishedReleases() => throw new NotSupportedException();
        public Task<AppBundle> ExportRelease(string version, string digest) => throw new NotSupportedException();
        public Task<AppSnapshot> Import(AppBundle bundle, AppOrigin? origin = null, AppInstallReceipt? installation = null) => throw new NotSupportedException();
        public Task<AppSnapshot> Replace(AppBundle bundle, AppOrigin? origin = null) => throw new NotSupportedException();
        public Task Delete() => throw new NotSupportedException();
}
