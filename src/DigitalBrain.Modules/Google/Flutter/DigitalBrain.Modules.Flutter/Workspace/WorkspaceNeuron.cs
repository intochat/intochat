using DigitalBrain.Flutter.Space;
using DigitalBrain.Flutter.Workspace.Signals;
using DigitalBrain.Framework;

namespace DigitalBrain.Flutter.Workspace;

[GrainType("ui.workspace")]
internal sealed class WorkspaceNeuron(
    [PersistentState("workspace", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<WorkspaceStorage> store)
    : RevisionedNeuron<WorkspaceStorage>(store), IWorkspace
{
    private string Brain => this.GetPrimaryKeyString();
    private WorkspaceStorage Seed(WorkspaceStorage state) => state.Path.Length == 0
        ? state with { Path = [Brain, Brain + "/apps/ino"], Open = [Brain + "/apps/ino"] } : state;

    public Task<WorkspaceState> Show(string app, string operationId, long expectedRevision)
    {
        ValidateKey(app);
        return Change(operationId, expectedRevision, state => state with
        {
            Path = state.Path[^1] == app ? state.Path : [.. state.Path, app],
            Open = IsApp(app) && !state.Open.Contains(app, StringComparer.Ordinal) ? [.. state.Open, app] : state.Open,
        });
    }

    public Task<WorkspaceState> Back(string operationId, long expectedRevision)
        => Change(operationId, expectedRevision, state => state with { Path = state.Path.Length > 1 ? state.Path[..^1] : state.Path });

    public Task<WorkspaceState> Home(string operationId, long expectedRevision)
        => Change(operationId, expectedRevision, state => state with { Path = [Brain] });

    public Task<WorkspaceState> Close(string app, string operationId, long expectedRevision)
    {
        ValidateKey(app);
        return Change(operationId, expectedRevision, state =>
        {
            var at = Array.FindIndex(state.Path, key => key == app || key.StartsWith(app + "/", StringComparison.Ordinal));
            return state with
            {
                Open = [.. state.Open.Where(key => key != app)],
                Path = at > 0 ? state.Path[..at] : state.Path,
            };
        });
    }

    public Task<WorkspaceState> Read()
    {
        var state = Seed(Snapshot);
        return Task.FromResult(new WorkspaceState(state.Revision, state.Path, state.Open));
    }

    private async Task<WorkspaceState> Change(string operationId, long expectedRevision, Func<WorkspaceStorage, WorkspaceStorage> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (operationId.Length > 256) { throw new ArgumentException("An operation id has at most 256 characters.", nameof(operationId)); }
        if (await Commit(operationId, expectedRevision, state => change(Seed(state)), state => new WorkspaceChanged(Brain, state.Revision)))
        { await GrainFactory.GetGrain<ISpace>(Brain).Rebuild(); }
        return await Read();
    }

    private bool IsApp(string key) => key.StartsWith(Brain + "/apps/", StringComparison.Ordinal) && !key[(Brain.Length + 6)..].Contains('/');

    private void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var segments = key.Split('/');
        if (key.Length > 512 || segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        { throw new ArgumentException("Provide a safe neuron key.", nameof(key)); }
        var friend = key.EndsWith("/profile", StringComparison.Ordinal) || key.Contains("/posts/", StringComparison.Ordinal);
        if (!key.StartsWith(Brain + "/", StringComparison.Ordinal) && !friend)
        { throw new ArgumentException("A workspace shows its own neurons or a member profile or post.", nameof(key)); }
    }
}

[GenerateSerializer]
internal sealed record WorkspaceStorage : IRevisioned<WorkspaceStorage>
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public string[] Operations { get; init; } = [];
    [Id(2)] public string[] Path { get; init; } = [];
    [Id(3)] public string[] Open { get; init; } = [];
    public WorkspaceStorage Advanced(string operationId) => this with
    { Revision = checked(Revision + 1), Operations = [.. Operations.TakeLast(63), operationId] };
}
