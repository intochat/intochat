using DigitalBrain.Flutter.Workspace.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Flutter.Workspace;

[GrainType("ui.workspace")]
internal sealed class WorkspaceNeuron(
    [PersistentState("workspace", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<WorkspaceStorage> store, IOptions<FlutterModuleOptions> options)
    : Neuron, IWorkspace
{
    public async Task<WorkspaceOpenResult> Open(OpenWindow request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.OperationId, 256, nameof(request.OperationId));
        Validate(request.WindowId, 256, nameof(request.WindowId));
        Validate(request.Title, 200, nameof(request.Title));
        ArgumentNullException.ThrowIfNull(request.Reference);
        ValidateReference(request.Reference.Kind, request.Reference.NeuronId, allowTable: true);
        var receipt = new WorkspaceReceipt("open", request.WindowId, request.Title, request.Reference, 0);
        if (Replay(request.OperationId, receipt) is { } previous) { return new(Snapshot(), previous.AppliedRevision); }
        RequireRevision(request.ExpectedRevision);
        if (store.State.Windows.TryGetValue(request.WindowId, out var existing) && existing.Reference != request.Reference)
        { throw new InvalidOperationException("A window ID cannot be reassigned to another neuron."); }
        var windows = new Dictionary<string, WorkspaceWindow>(store.State.Windows, StringComparer.Ordinal)
        { [request.WindowId] = new(request.WindowId, request.Title, request.Reference, true) };
        var next = Record(request.OperationId, receipt) with { Windows = windows, FocusedWindow = request.WindowId };
        await Save(next);
        return new(Snapshot(), next.Revision);
    }

    public async Task<WorkspaceState> Focus(string? windowId, string operationId, long expectedRevision)
    {
        Validate(operationId, 256, nameof(operationId));
        if (windowId is not null) { Validate(windowId, 256, nameof(windowId)); }
        var receipt = new WorkspaceReceipt("focus", windowId, null, null, 0);
        if (Replay(operationId, receipt) is not null) { return Snapshot(); }
        RequireRevision(expectedRevision);
        if (windowId is not null && (!store.State.Windows.TryGetValue(windowId, out var window) || !window.IsOpen))
        { throw new KeyNotFoundException("The workspace has no such open window."); }
        await Save(Record(operationId, receipt) with { FocusedWindow = windowId });
        return Snapshot();
    }

    public async Task<WorkspaceState> Close(string windowId, long expectedRevision, string operationId)
    {
        Validate(windowId, 256, nameof(windowId));
        Validate(operationId, 256, nameof(operationId));
        var receipt = new WorkspaceReceipt("close", windowId, null, null, 0);
        if (Replay(operationId, receipt) is not null) { return Snapshot(); }
        RequireRevision(expectedRevision);
        if (!store.State.Windows.TryGetValue(windowId, out var window))
        { throw new KeyNotFoundException("The workspace has no such window."); }
        var windows = new Dictionary<string, WorkspaceWindow>(store.State.Windows, StringComparer.Ordinal)
        { [windowId] = window with { IsOpen = false } };
        await Save(Record(operationId, receipt) with
        {
            Windows = windows,
            FocusedWindow = store.State.FocusedWindow == windowId ? null : store.State.FocusedWindow,
        });
        return Snapshot();
    }

    public async Task<WorkspaceState> Configure(UiChildRef? home, UiChildRef? companion, long expectedRevision)
    {
        if (home is not null) { ValidateReference(home.Kind, home.Name); }
        if (companion is not null) { ValidateReference(companion.Kind, companion.Name); }
        RequireRevision(expectedRevision);
        if (store.State.Home == home && store.State.Companion == companion) { return Snapshot(); }
        await Save(store.State with { Revision = checked(store.State.Revision + 1), Home = home, Companion = companion });
        return Snapshot();
    }

    public async Task<WorkspaceState> SetConnectedSources(IReadOnlyList<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var connected = sources.Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (connected.SequenceEqual(store.State.ConnectedSources, StringComparer.OrdinalIgnoreCase)) { return Snapshot(); }
        await Save(store.State with { Revision = checked(store.State.Revision + 1), ConnectedSources = connected });
        return Snapshot();
    }

    public Task<WorkspaceState> Read() => Task.FromResult(Snapshot());

    private WorkspaceState Snapshot() => new(store.State.Revision, store.State.Windows.Values.ToArray(),
        store.State.Windows.Count == 0 ? WorkspaceStarterCatalog.Build(store.State.ConnectedSources) with { AssistantTitle = options.Value.AssistantTitle } : null,
        store.State.FocusedWindow, store.State.Home, store.State.Companion);

    private WorkspaceReceipt? Replay(string operationId, WorkspaceReceipt receipt)
    {
        if (!store.State.Receipts.TryGetValue(operationId, out var previous)) { return null; }
        if (previous with { AppliedRevision = 0 } != receipt)
        { throw new InvalidOperationException("This operation ID already belongs to another workspace command."); }
        return previous;
    }

    private WorkspaceStorage Record(string operationId, WorkspaceReceipt receipt)
    {
        var revision = checked(store.State.Revision + 1);
        return store.State with
        {
            Revision = revision,
            Receipts = new(store.State.Receipts, StringComparer.Ordinal) { [operationId] = receipt with { AppliedRevision = revision } },
        };
    }

    private void ValidateReference(string kind, string name, bool allowTable = false)
    {
        Validate(kind, 64, nameof(kind));
        Validate(name, 256, nameof(name));
        if (!(allowTable && kind == WindowReference.TableKind) && !name.StartsWith(this.GetPrimaryKeyString() + "/", StringComparison.Ordinal))
        { throw new ArgumentException("A workspace entry must reference a neuron in this workspace."); }
    }

    private async Task Save(WorkspaceStorage next)
    {
        var previous = store.State;
        store.State = next;
        try { await store.WriteStateAsync(); }
        catch { store.State = previous; throw; }
        await PublishAsync(new WorkspaceChanged(this.GetPrimaryKeyString(), next.Revision));
    }
    private void RequireRevision(long revision)
    {
        if (store.State.Revision != revision)
        { throw new RevisionConflictException($"Expected revision {revision}; current revision is {store.State.Revision}.", store.State.Revision); }
    }
    private static void Validate(string value, int limit, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit)
        { throw new ArgumentException($"Provide {name} with 1-{limit} characters.", name); }
    }
}

[GenerateSerializer]
internal sealed record WorkspaceStorage
{
    [Id(0)] public long Revision { get; init; }
    [Id(1)] public Dictionary<string, WorkspaceWindow> Windows { get; init; } = new(StringComparer.Ordinal);
    [Id(2)] public Dictionary<string, WorkspaceReceipt> Receipts { get; init; } = new(StringComparer.Ordinal);
    [Id(3)] public string[] ConnectedSources { get; init; } = [];
    [Id(4)] public string? FocusedWindow { get; init; }
    [Id(5)] public UiChildRef? Home { get; init; }
    [Id(6)] public UiChildRef? Companion { get; init; }
}

[GenerateSerializer]
internal sealed record WorkspaceReceipt(
    [property: Id(0)] string Command,
    [property: Id(1)] string? WindowId,
    [property: Id(2)] string? Title,
    [property: Id(3)] WindowReference? Reference,
    [property: Id(4)] long AppliedRevision);
