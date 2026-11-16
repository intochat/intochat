using System.Globalization;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Errors;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.Files;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Primitives;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Fields;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

[Signal("files.workspace-changed")]
internal sealed record FilesWorkspaceChanged(long Revision) : DigitalBrain.Signal;

// An operation this workspace started: its id and label. Progress and result are read from the brain's operations query, not
// mirrored here; Done records that its completion was applied (folder refreshed, synapse removed).
internal sealed record FilesStartedOperation(string Id, string Label, bool Done = false);
internal sealed record FilesWorkspaceState
{
    public FilesState Files { get; init; } = new();
    public Guid[] Handled { get; init; } = [];
    public FilesStartedOperation[] Started { get; init; } = [];
    public long ViewVersion { get; init; }
    public int PageOffset { get; init; }
    public string? Notice { get; init; }
    public string? PendingRequest { get; init; }
    public bool Ready { get; init; }
    public int Width { get; init; } = 1440;
    public bool PreviewOpen { get; init; }
    public string ViewMode { get; init; } = "list";
}

// The provider-neutral app owns navigation; this host owns durable request receipts, runtime operations and UI delivery.
[Neuron("apps.files", State = "files-workspace")]
internal sealed partial class FilesWorkspaceNeuron(
    ICapabilityBroker broker,
    IOperations operations,
    IOperationsQuery operationsQuery,
    ISignalHistory history,
    ILogger<FilesWorkspaceNeuron> logger,
    IServiceProvider services) : Neuron<FilesWorkspaceState>, IFilesWorkspace
{
    private string AppPath => Id.Key.EndsWith("/workspace", StringComparison.Ordinal) && Id.Key.StartsWith("apps/", StringComparison.Ordinal)
        ? Id.Key[..^"/workspace".Length] : throw new InvalidOperationException("Files workspace keys belong to an app.");
    private string AppKey => Id.Brain.Value + "/" + AppPath;
    private FilesApp App => new(broker is CapabilityBroker runtime ? runtime.ForApp(AppKey) : broker, Id.Brain, State.Files);
    private IView View(string slot) => Get<IView>(new(Id.Brain, UIVocabulary.ViewType, AppPath + "/" + slot));
    private IField Field(string name) => Get<IField>(new(Id.Brain, UIVocabulary.FieldType, AppPath + "/" + name));

    public Task<long> Revision() { RequireAccess(); return Task.FromResult(State.Files.Revision); }

    public async Task Open(CancellationToken ct = default)
    {
        RequireAccess();
        var app = App;
        if (State.Ready && State.Files.Connection is { } currentConn)
        {
            await app.Browse(new(currentConn, State.Files.Folder), ct).ConfigureAwait(false);
        }
        else
        {
            await app.Open(new(), ct).ConfigureAwait(false);
        }
        var nextRevision = Math.Max(1, State.Files.Revision + 1);
        await Persist(State with { Files = app.Snapshot with { Revision = nextRevision }, Ready = true });
        await Recover(ct);
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        if (!State.Ready) { return; }
        await WithAppCallerAsync(async () =>
        {
            if (State.PendingRequest is not null)
            {
                await Persist(State with
                {
                    PendingRequest = null,
                    Files = State.Files with { Revision = checked(State.Files.Revision + 1) },
                    Notice = "An interrupted request was not repeated. Check the folder before retrying.",
                });
            }
            await Recover(cancellationToken);
        });
    }

    private async Task Recover(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Field("search").Configure(DigitalBrain.UI.FieldKind.Text);
        await Field("filter").Configure(DigitalBrain.UI.FieldKind.Text);
        await Field("name").Configure(DigitalBrain.UI.FieldKind.Text);
        await Field("content").Configure(DigitalBrain.UI.FieldKind.Multiline);
        foreach (var field in new[] { "search", "filter" })
        { await Context.Synapses.Ensure<FieldChanged>(new(Id.Brain, UIVocabulary.FieldType, AppPath + "/" + field)); }
        foreach (var operation in State.Started.Where(item => !item.Done)) { await FollowOperation(operation.Id, ct); }
        await Render();
    }

    public async Task Request(string operation, FilesWorkspaceInput input, long expectedRevision, Guid requestId, CancellationToken ct = default)
    {
        RequireAccess();
        ArgumentNullException.ThrowIfNull(input);
        if (requestId == Guid.Empty) { throw new ArgumentException("A request id is required.", nameof(requestId)); }
        if (State.Handled.Contains(requestId)) { return; }
        if (expectedRevision != State.Files.Revision && operation is not "ask-ino") { throw new StaleActionException("Files changed. Read the current view before acting."); }
        ct.ThrowIfCancellationRequested();
        // This receipt and revision reach durable state before any operation starts; replay cannot become another write.
        await Persist(State with
        {
            Files = State.Files with { Revision = checked(State.Files.Revision + 1) },
            Handled = [.. State.Handled.TakeLast(255), requestId],
            Notice = null,
        });
        var app = App;
        try
        {
            switch (operation)
            {
                case "open": await app.Open(new(), ct); await Store(app, resetPage: true); break;
                case "storage":
                    var storage = At(State.Files.Storages, input.Index);
                    if (storage.Error is not null) { app.ShowUnavailable(storage.Connection); }
                    else { await app.Browse(new(storage.Connection), ct); }
                    await Store(app, resetPage: true); break;
                case "browse":
                    var item = At(State.Files.Items, input.Index);
                    await app.Browse(new(item.Connection, item.Item.Id), ct); await Store(app, resetPage: true); break;
                case "breadcrumb":
                    var parent = At(State.Files.Breadcrumbs, input.Index);
                    await app.Browse(new(parent.Connection, parent.Item.Id), ct); await Store(app, resetPage: true); break;
                case "select":
                    await app.Select(new(At(State.Files.Items, input.Index)), ct);
                    await Store(app);
                    if (State.Width <= 390) { await Persist(State with { PreviewOpen = true }); }
                    break;
                case "preview": await app.Preview(new(), ct); await Store(app, resetPage: true); break;
                case "up":
                    if (State.Files.Breadcrumbs.Length > 1)
                    {
                        var up = State.Files.Breadcrumbs[^2]; await app.Browse(new(up.Connection, up.Item.Id), ct);
                    }
                    else if (State.Files.Connection is { } connection) { await app.Browse(new(connection), ct); }
                    else { await app.Open(new(), ct); }
                    await Store(app, resetPage: true); break;
                case "search": await app.Search(new(input.Query ?? (await Field("search").Read()).Value ?? "", input.Everywhere), ct); await Store(app, resetPage: true); break;
                case "more":
                    var offset = State.Files.Items.Length;
                    await app.More(new(), ct);
                    await Persist(State with { Files = app.Snapshot, PageOffset = app.Snapshot.Items.Length > offset ? offset : State.PageOffset }); break;
                case "previous": await Persist(State with { PageOffset = Math.Max(0, State.PageOffset - 40) }); break;
                case "next": await Persist(State with { PageOffset = Math.Min(Math.Max(0, State.Files.Items.Length - 1) / 40 * 40, State.PageOffset + 40) }); break;
                case "copy": app.Copy(move: false); await Store(app); break;
                case "move": app.Copy(move: true); await Store(app); break;
                case "ask-ino":
                    var prompt = app.AskIno();
                    await Store(app);
                    try
                    {
                        var enrichedPrompt = !string.IsNullOrWhiteSpace(app.Snapshot.Preview) && !string.Equals(app.Snapshot.Preview, FilesCopy.PreviewUnavailable, StringComparison.Ordinal) ? $"{prompt}\n\n[File Preview]\n{(app.Snapshot.Preview.Length <= 1200 ? app.Snapshot.Preview : app.Snapshot.Preview[..1200])}" : prompt;
                        var edge = services.GetService<EdgeAuthority>();
                        using (edge?.EnterInProcess())
                        {
                            await DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons.Get<IApp>(services, IntoChat.OS.Primitives.AppRef.Of(Id.Brain, ShippedApps.Ino).Key).Request("send", enrichedPrompt);
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        logger.LogDebug(error, "Ino handoff signal could not be forwarded in-process.");
                    }
                    break;
                case "fail": app.Options(ConflictPolicy.Fail, State.Files.DryRun); await Store(app); break;
                case "keep-both": app.Options(ConflictPolicy.Rename, State.Files.DryRun); await Store(app); break;
                case "replace": app.Options(ConflictPolicy.Overwrite, State.Files.DryRun); await Store(app); break;
                case "dry-run": app.Options(State.Files.Conflict, !State.Files.DryRun); await Store(app); break;
                case "mkdir" or "write" or "rename" or "delete" or "paste":
                    if (State.Started.Count(item => !item.Done) >= 8) { throw new InvalidOperationException("Wait for a running operation before starting another."); }
                    var name = input.Name ?? (await Field("name").Read()).Value ?? "";
                    if (operation is "mkdir" or "write" or "rename")
                    {
                        var nameRule = FilesPathRules.ValidateSingleSegmentName(name);
                        if (nameRule.Broken)
                        {
                            throw new ArgumentException(nameRule.Message);
                        }
                        SafeRelativePath.TryParse(name, out var safeName);
                        name = safeName.Value;
                    }
                    var text = operation == "write" ? input.Text ?? (await Field("content").Read()).Value ?? "" : "";
                    var sizeRule = FilesPathRules.ValidateInlineTextSize(text);
                    if (sizeRule.Broken) { throw new InvalidOperationException(sizeRule.Message); }
                    var plan = app.Plan(operation, name, text);
                    await Persist(State with { PendingRequest = operation });
                    var started = await operations.Start(plan, ct);
                    await Persist(State with
                    {
                        PendingRequest = null,
                        Started = [.. State.Started.Where(item => !item.Done).Concat(State.Started.Where(item => item.Done).TakeLast(23)),
                            new(started.Id.Value, plan.DryRun ? "Dry run" : operation == "delete" ? "Move to trash" : operation)],
                    });
                    await FollowOperation(started.Id.Value, ct); break;
                case "cancel":
                    var cancelling = At(State.Started, input.Index);
                    if (!cancelling.Done) { await operations.Cancel(new(cancelling.Id), ct); }
                    break;
                case "filter": app.Filter(input.Query ?? (await Field("filter").Read()).Value ?? ""); await Store(app); break;
                case "clear-filter": app.Filter(""); await Store(app); break;
                case "sort-name": app.SortBy("name"); await Store(app); break;
                case "sort-size": app.SortBy("size"); await Store(app); break;
                case "sort-modified": app.SortBy("modified"); await Store(app); break;
                case "view-list": await Persist(State with { ViewMode = "list" }); break;
                case "view-grid": await Persist(State with { ViewMode = "grid" }); break;
                case "back-folder": await app.BackToFolder(ct); await Store(app, resetPage: true); break;
                case "back-files": await Persist(State with { PreviewOpen = false }); break;
                case "retry":
                    if (State.Files.Searching && State.Files.Search is { } again) { await app.Search(new(again, Everywhere: true), ct); }
                    else if (State.Files.Connection is { } current) { await app.Browse(new(current, State.Files.Folder), ct); }
                    else { await app.Open(new(), ct); }
                    await Store(app, resetPage: true); break;
                case "open-settings":
                    await FilesHost.EnsureDefaultLocalRootsAsync(services, Id.Brain.Value);
                    await app.Open(new(), ct);
                    if (app.Snapshot.Storages.FirstOrDefault(s => s.Error is null) is { } healthyStorage)
                    {
                        await app.Browse(new(healthyStorage.Connection), ct);
                    }
                    await Store(app, resetPage: true);
                    break;
                case "upload" or "download" or "open-app": break;
                default: throw new ArgumentException("Unknown Files action.", nameof(operation));
            }
        }
        catch (StorageException error) { await Persist(State with { PendingRequest = null, Notice = "Storage: " + error.Code }); }
        catch (ArgumentException error) { await Persist(State with { PendingRequest = null, Notice = error.Message }); }
        catch (InvalidOperationException error) { await Persist(State with { PendingRequest = null, Notice = error.Message }); }
        await Render();
    }

    [On]
    public async Task On(FieldChanged changed)
    {
        var full = changed.Owner + "/" + changed.Name;
        var field = full == AppKey + "/filter" ? "filter" : full == AppKey + "/search" ? "search" : null;
        if (field is null) { return; }
        var value = (await Field(field).Read()).Value ?? "";
        if (field == "filter")
        {
            if (string.Equals(State.Files.Filter, value, StringComparison.Ordinal)) { return; }
            await WithAppCallerAsync(async () =>
            {
                try { await Request("filter", new(Query: value), State.Files.Revision, Guid.NewGuid()); }
                catch (StaleActionException error)
                {
                    logger.LogDebug(error, "A files filter action was stale.");
                }
            });
        }
        else if (field == "search" && !string.IsNullOrWhiteSpace(value))
        {
            if (State.Files.Searching && string.Equals(State.Files.Search, value, StringComparison.Ordinal)) { return; }
            await WithAppCallerAsync(async () =>
            {
                try { await Request("search", new(Query: value, Everywhere: true), State.Files.Revision, Guid.NewGuid()); }
                catch (StaleActionException error)
                {
                    logger.LogDebug(error, "A files search action was stale.");
                }
            });
        }
    }

    [On]
    public async Task On(OperationProgress progress)
    {
        if (!State.Started.Any(item => item.Id == progress.Operation.Value && !item.Done)) { return; }
        await WithAppCallerAsync(async () =>
        {
            // The fraction itself is read from the operations query when the view renders.
            await Persist(State);
            await Render();
        });
    }

    [On]
    public async Task On(OperationDone done)
    {
        if (!State.Started.Any(item => item.Id == done.Operation.Value && !item.Done)) { return; }
        await WithAppCallerAsync(async () =>
        {
            await Persist(State with
            {
                Files = State.Files with { Revision = checked(State.Files.Revision + 1) },
                Started = State.Started.Select(item => item.Id == done.Operation.Value ? item with { Done = true } : item).ToArray(),
            });
            if (State.Files.Connection is { } connection)
            {
                try { var app = App; await app.Browse(new(connection, State.Files.Folder)); await Store(app, resetPage: true); }
                catch (StorageException error) { await Persist(State with { Notice = "Storage: " + error.Code }); }
            }
            await Context.Synapses.Remove(new(Id.Brain, "runtime.operation", done.Operation.Value));
            await Render();
        });
    }

    private async Task WithAppCallerAsync(Func<Task> action)
    {
        var edge = services.GetService<EdgeAuthority>();
        using (edge?.EnterInProcess())
        {
            await action();
        }
    }

    private async Task FollowOperation(string operation, CancellationToken ct = default)
    {
        var source = new DigitalBrain.NeuronId(Id.Brain, "runtime.operation", operation);
        // Ensure starts a fresh cursor at the tail. Read durable history after subscribing so a fast completion cannot fall
        // between Start and Watch; subsequent delivery is harmless because completed operations are applied only once.
        await Context.Synapses.Ensure(source, typeof(OperationProgress), typeof(OperationDone));
        await operations.Reconcile(new(operation), ct);
        long since = 0;
        OperationProgress? latest = null;
        while (true)
        {
            var page = await history.Read(source, since, 128, ct);
            foreach (var entry in page.Signals)
            {
                if (entry.Signal is OperationDone done && done.Operation.Value == operation) { await On(done); return; }
                if (entry.Signal is OperationProgress progress && progress.Operation.Value == operation) { latest = progress; }
                since = entry.Sequence;
            }
            if (page.Signals.Count == 0 || since >= page.Last) { break; }
        }
        if (latest is not null) { await On(latest); }
    }
    private Task Store(FilesApp app, bool resetPage = false) => Persist(State with { Files = app.Snapshot, PageOffset = resetPage ? 0 : State.PageOffset });
    private Task Persist(FilesWorkspaceState next) => Save(next with { ViewVersion = checked(State.ViewVersion + 1) }, new FilesWorkspaceChanged(next.Files.Revision));
    private static T At<T>(T[] items, int? index) => index is >= 0 && index < items.Length ? items[index.Value] : throw new StaleActionException("This item is no longer available.");

    private void RequireAccess()
    {
        var caller = Context.Caller ?? throw new UnauthorizedAccessException("No caller context was stamped at a trusted edge.");
        if (caller.Actor is not Actor.Kernel && !string.Equals(caller.Brain.Value, Id.Brain.Value, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"A caller of brain {caller.Brain.Value} may not touch this Files workspace.");
        }
        if (caller.Actor is Actor.App app && !string.Equals(app.Key, AppKey, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"An app caller {app.Key} may not touch this Files workspace.");
        }
    }
}
