/*
# Files
Role: Shell
Shipped: true
Operations: run

Browse, search, create and manage files across connected storages, with safe confirmations. One screen (`app.files`): storages
in the sidebar, the folder or search results with a quick view, an inline prompt for a new name, an editor for a selected text
file, and the operations it started. Every button sends `files.run` with the revision it was drawn at; every field sends `files.type`
with what the person typed.
*/
using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Contracts;
using DigitalBrain.Errors;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.UI;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

// A request a Files button sends to the screen: the verb, the revision the person saw and the item index it names.
[Signal(Alias)]
internal sealed record FilesRun(string Operation, long Revision, int? Index = null, bool Everywhere = false) : DigitalBrain.Signal
{
    internal const string Alias = "files.run";
}

// What a person typed into one Files field, sent after each pause in typing: the screen keeps it in its state until the request
// that uses it. Search and filter run as they change.
[Signal(Alias)]
internal sealed record FilesType(string Field, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Value = null) : DigitalBrain.Signal
{
    internal const string Alias = "files.type";
}

// The text of each Files field as the person last typed it; null when nothing was typed (a field then shows what the view decides).
internal sealed record FilesTyped(string? Name = null, string? Content = null, string? Prompt = null, string? Editor = null, string? Search = null, string? Filter = null);

// What a request carries besides its verb: an index, or the text a field or a prompt gave.
internal sealed record FilesWorkspaceInput(
    int? Index = null,
    string? Name = null,
    string? Text = null,
    string? Query = null,
    bool Everywhere = false);

// An operation this workspace started: its id and label, and what its signals reported (the fraction while it runs, the status once
// done). Done records that its completion was applied (folder refreshed, wire removed).
internal sealed record FilesStartedOperation(string Id, string Label, bool Done = false, double Fraction = 0, string? Status = null, bool Failed = false);

internal sealed record FilesWorkspaceState
{
    public FilesState Files { get; init; } = new();
    public FilesStartedOperation[] Started { get; init; } = [];
    public int PageOffset { get; init; }
    public string? Notice { get; init; }
    // An operation between its plan and its start: on reactivation it is reported, never repeated.
    public string? PendingRequest { get; init; }
    // "mkdir" or "rename" while the inline prompt asks for a name.
    public string? Prompt { get; init; }
    public bool Ready { get; init; }
    public int Width { get; init; } = 1440;
    public bool PreviewOpen { get; init; }
    public bool QuickWriteCollapsed { get; init; }
    public string ViewMode { get; init; } = "list";
    public FilesTyped Typed { get; init; } = new();
}

// The Files app: one screen of the brain (app.files/default). The provider-neutral FilesApp owns navigation; this screen owns its
// state, the operations it started and its view. Every button sends FilesRun to "." through POST /act; every field sends FilesType
// with its text, which the screen keeps in its state (Typed) until the request that uses it.
[Neuron("app.files", State = "files-workspace")]
[Screen("app.files", Title = "Files", Icon = "folder", Category = "Workspace", Dock = 3)]
[Description("Browse, search, create and manage files across connected storages, with safe confirmations.")]
internal sealed partial class FilesWorkspaceScreen(
    ICapabilityBroker broker,
    IOperations operations,
    ISignalHistory history,
    ILogger<FilesWorkspaceScreen> logger,
    IServiceProvider services) : Screen<FilesWorkspaceState>
{
    public static AppBundle Bundle => AppBundle.FromScreen<FilesWorkspaceScreen>(
        FilesApp.AppName,
        "4.0.0",
        requires: new(Capabilities: ["storage@1"]));

    internal const string AppPath = "apps/" + FilesApp.AppName;
    internal const string NameField = "name";
    internal const string ContentField = "content";
    internal const string SearchField = "search";
    internal const string FilterField = "filter";
    internal const string PromptField = "prompt";
    internal const string EditorField = "editor";

    // A preview longer than this is not offered for editing: the view carries the editor's text.
    internal const int MaxEditableText = 32_000;

    // What one field may hold: the largest inline write (FilesPathRules.ValidateInlineTextSize) in characters.
    internal const int MaxTyped = 256 * 1024;

    private string AppKey => Id.Brain.Value + "/" + AppPath;
    private FilesApp App => new(broker is CapabilityBroker runtime ? runtime.ForApp(AppKey) : broker, Id.Brain, State.Files);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);
        if (!State.Ready)
        {
            var app = App;
            await app.Open(new(), cancellationToken);
            await Show(State with { Files = app.Snapshot with { Revision = Math.Max(1, State.Files.Revision + 1) }, Ready = true });
            return;
        }
        if (State.PendingRequest is not null)
        {
            await Show(State with
            {
                PendingRequest = null,
                Files = State.Files with { Revision = checked(State.Files.Revision + 1) },
                Notice = "An interrupted request was not repeated. Check the folder before retrying.",
            });
        }
        foreach (var operation in State.Started.Where(item => !item.Done)) { await FollowOperation(operation.Id, cancellationToken); }
    }

    [On]
    [Description("Run a Files verb (open, storage, browse, select, search, preview, up, mkdir, write, rename, delete, copy, move, paste, ...) on the revision the person saw.")]
    public async Task Run(FilesRun request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireSameBrain();
        await Request(request.Operation, new FilesWorkspaceInput(Index: request.Index, Everywhere: request.Everywhere), request.Revision);
    }

    private async Task Request(string operation, FilesWorkspaceInput input, long expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision != State.Files.Revision && operation is not "ask-ino") { throw new StaleActionException("Files changed. Read the current view before acting."); }
        ct.ThrowIfCancellationRequested();
        var app = App;
        // A drawn revision is good for one request: the next view carries the next one.
        var next = State with { Files = State.Files with { Revision = checked(State.Files.Revision + 1) }, Notice = null };
        try
        {
            next = await Apply(operation, input, next, app, ct);
        }
        // A refused request ends what it started: nothing is pending and no prompt stays open.
        catch (StorageException error) { next = next with { PendingRequest = null, Prompt = null, Notice = "Storage: " + error.Code }; }
        catch (ArgumentException error) { next = next with { PendingRequest = null, Prompt = null, Notice = error.Message }; }
        catch (InvalidOperationException error) { next = next with { PendingRequest = null, Prompt = null, Notice = error.Message }; }
        await Show(next);
    }

    private async Task<FilesWorkspaceState> Apply(string operation, FilesWorkspaceInput input, FilesWorkspaceState next, FilesApp app, CancellationToken ct)
    {
        var revision = next.Files.Revision;
        FilesWorkspaceState Stored(bool resetPage = false) => next with
        {
            Files = app.Snapshot with { Revision = revision },
            PageOffset = resetPage ? 0 : next.PageOffset,
        };

        switch (operation)
        {
            case "open": await app.Open(new(), ct); return Stored(resetPage: true);
            case "storage":
                var storage = At(next.Files.Storages, input.Index);
                if (storage.Error is not null) { app.ShowUnavailable(storage.Connection); }
                else { await app.Browse(new(storage.Connection), ct); }
                return Stored(resetPage: true);
            case "browse":
                var item = At(next.Files.Items, input.Index);
                await app.Browse(new(item.Connection, item.Item.Id), ct); return Stored(resetPage: true);
            case "breadcrumb":
                var parent = At(next.Files.Breadcrumbs, input.Index);
                await app.Browse(new(parent.Connection, parent.Item.Id), ct); return Stored(resetPage: true);
            case "select":
                await app.Select(new(At(next.Files.Items, input.Index)), ct);
                var selected = Stored() with { PreviewOpen = next.PreviewOpen || next.Width <= 390 };
                // A newly selected file opens in the editor as it is stored.
                return selected with { Typed = selected.Typed with { Editor = null } };
            case "preview": await app.Preview(new(), ct); return Stored(resetPage: true);
            case "up":
                if (next.Files.Breadcrumbs.Length > 1)
                {
                    var up = next.Files.Breadcrumbs[^2]; await app.Browse(new(up.Connection, up.Item.Id), ct);
                }
                else if (next.Files.Connection is { } connection) { await app.Browse(new(connection), ct); }
                else { await app.Open(new(), ct); }
                return Stored(resetPage: true);
            case "search":
                await app.Search(new(input.Query ?? next.Typed.Search ?? "", input.Everywhere), ct);
                return Stored(resetPage: true);
            case "more":
                var offset = next.Files.Items.Length;
                await app.More(new(), ct);
                return Stored() with { PageOffset = app.Snapshot.Items.Length > offset ? offset : next.PageOffset };
            case "previous": return next with { PageOffset = Math.Max(0, next.PageOffset - 40) };
            case "next": return next with { PageOffset = Math.Min(Math.Max(0, next.Files.Items.Length - 1) / 40 * 40, next.PageOffset + 40) };
            case "copy": app.Copy(move: false); return Stored() with { QuickWriteCollapsed = false };
            case "move": app.Copy(move: true); return Stored() with { QuickWriteCollapsed = false };
            case "toggle-quick-write": return next with { QuickWriteCollapsed = !next.QuickWriteCollapsed };
            case "ask-ino":
                var prompt = app.AskIno();
                await HandToIno(app.Snapshot, prompt);
                return Stored();
            case "fail": app.Options(ConflictPolicy.Fail, next.Files.DryRun); return Stored();
            case "keep-both": app.Options(ConflictPolicy.Rename, next.Files.DryRun); return Stored();
            case "replace": app.Options(ConflictPolicy.Overwrite, next.Files.DryRun); return Stored();
            case "dry-run": app.Options(next.Files.Conflict, !next.Files.DryRun); return Stored();
            case "prompt-confirm":
                var pending = next.Prompt ?? throw new InvalidOperationException("Nothing is waiting for a name.");
                var given = (next.Typed.Prompt ?? "").Trim();
                next = next with { Prompt = null };
                if (given.Length == 0) { return next with { Notice = "A name is required." }; }
                return await Start(pending, input with { Name = given }, next, app, ct);
            case "prompt-cancel": return next with { Prompt = null };
            case "save-edit":
                var edited = next.Files.Selected ?? throw new InvalidOperationException("Select a file to save.");
                var content = next.Typed.Editor;
                if (content is null || string.Equals(content, next.Files.Preview, StringComparison.Ordinal)) { return next with { Notice = "Nothing changed." }; }
                return await Start("write", input with { Name = edited.Item.Name, Text = content }, next, app, ct, replace: true);
            case "mkdir" or "write" or "rename" or "delete" or "paste":
                return await Start(operation, input, next, app, ct);
            case "cancel":
                var cancelling = At(next.Started, input.Index);
                if (!cancelling.Done) { await operations.Cancel(new(cancelling.Id), ct); }
                return next;
            case "filter": app.Filter(input.Query ?? next.Typed.Filter ?? ""); return Stored();
            case "clear-filter": app.Filter(""); return Stored();
            case "sort-name": app.SortBy("name"); return Stored();
            case "sort-size": app.SortBy("size"); return Stored();
            case "sort-modified": app.SortBy("modified"); return Stored();
            case "view-list": return next with { ViewMode = "list" };
            case "view-grid": return next with { ViewMode = "grid" };
            case "back-folder": await app.BackToFolder(ct); return Stored(resetPage: true);
            case "back-files": return next with { PreviewOpen = false };
            case "retry":
                if (next.Files.Searching && next.Files.Search is { } again) { await app.Search(new(again, Everywhere: true), ct); }
                else if (next.Files.Connection is { } current) { await app.Browse(new(current, next.Files.Folder), ct); }
                else { await app.Open(new(), ct); }
                return Stored(resetPage: true);
            case "open-settings":
                await FilesHost.EnsureDefaultLocalRootsAsync(services, Id.Brain.Value);
                await app.Open(new(), ct);
                if (app.Snapshot.Storages.FirstOrDefault(s => s.Error is null) is { } healthyStorage)
                {
                    await app.Browse(new(healthyStorage.Connection), ct);
                }
                return Stored(resetPage: true);
            case "download":
                return next with
                {
                    Notice = next.Files.PreviewUrl is { } dlUrl
                        ? $"Download ready: {dlUrl}"
                        : next.Files.Selected is { } sel
                            ? $"Select a file to download ({sel.Item.Name})."
                            : "Select a file to download.",
                };
            case "upload": return next with { Notice = "Drag-and-drop or use Quick Write to save a file into the current folder." };
            case "open-app":
                return next with
                {
                    Notice = next.Files.PreviewApp is { } appName
                        ? $"Smart App '{appName}' source folder selected."
                        : "Select a Smart App folder to open.",
                };
            default: throw new ArgumentException("Unknown Files action.", nameof(operation));
        }
    }

    // A write the operations gate runs: a name is asked first for a new folder or a rename that has none; the plan is recorded as
    // pending before it starts, so a reactivation in between reports it instead of repeating it. An edit saved over its own file
    // replaces it (the gate asks the person when its policy says so).
    private async Task<FilesWorkspaceState> Start(string operation, FilesWorkspaceInput input, FilesWorkspaceState next, FilesApp app, CancellationToken ct, bool replace = false)
    {
        if (next.Started.Count(item => !item.Done) >= 8) { throw new InvalidOperationException("Wait for a running operation before starting another."); }
        var name = input.Name ?? next.Typed.Name ?? "";
        if (operation is "mkdir" or "rename" && string.IsNullOrWhiteSpace(name))
        {
            // The prompt starts with the selected item's name for a rename, empty for a new folder.
            return next with { Prompt = operation, Typed = next.Typed with { Prompt = operation == "rename" ? next.Files.Selected?.Item.Name : null } };
        }
        if (operation is "mkdir" or "write" or "rename")
        {
            var nameRule = FilesPathRules.ValidateSingleSegmentName(name);
            if (nameRule.Broken) { throw new ArgumentException(nameRule.Message); }
            SafeRelativePath.TryParse(name, out var safeName);
            name = safeName.Value;
        }
        var text = operation == "write" ? input.Text ?? next.Typed.Content ?? "" : "";
        var sizeRule = FilesPathRules.ValidateInlineTextSize(text);
        if (sizeRule.Broken) { throw new InvalidOperationException(sizeRule.Message); }
        var plan = app.Plan(operation, name, text);
        if (replace) { plan = plan with { Conflict = ConflictPolicy.Overwrite }; }
        await Show(next with { PendingRequest = operation });
        var started = await operations.Start(plan, ct);
        var label = plan.DryRun ? "Dry run" : operation == "delete" ? "Move to trash" : operation;
        next = State with
        {
            PendingRequest = null,
            Started = [.. State.Started.Where(item => !item.Done).Concat(State.Started.Where(item => item.Done).TakeLast(23)), new(started.Id.Value, label)],
        };
        await Show(next);
        await FollowOperation(started.Id.Value, ct);
        return State;
    }

    [On]
    [Description("Keep what the person typed into a Files field (name, content, prompt, editor, search, filter); search and filter run as they change.")]
    public async Task Type(FilesType typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        RequireSameBrain();
        var value = typed.Value ?? "";
        if (value.Length > MaxTyped) { throw new ArgumentException("A Files field holds at most 262144 characters.", nameof(typed)); }
        var kept = State.Typed;
        switch (typed.Field)
        {
            case NameField: await Show(State with { Typed = kept with { Name = value } }); return;
            case ContentField: await Show(State with { Typed = kept with { Content = value } }); return;
            case PromptField: await Show(State with { Typed = kept with { Prompt = value } }); return;
            case EditorField: await Show(State with { Typed = kept with { Editor = value } }); return;
            case FilterField:
                await Show(State with { Typed = kept with { Filter = value } });
                // A filter reads the text, never the drawn rows: it runs on the current revision.
                if (!string.Equals(State.Files.Filter, value, StringComparison.Ordinal)) { await Request("filter", new(Query: value), State.Files.Revision); }
                return;
            case SearchField:
                await Show(State with { Typed = kept with { Search = value } });
                if (!string.IsNullOrWhiteSpace(value) && !(State.Files.Searching && string.Equals(State.Files.Search, value, StringComparison.Ordinal)))
                { await Request("search", new(Query: value, Everywhere: true), State.Files.Revision); }
                return;
            default: throw new ArgumentException("Unknown Files field.", nameof(typed));
        }
    }

    [On]
    public async Task On(OperationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!State.Started.Any(item => item.Id == progress.Operation.Value && !item.Done)) { return; }
        var fraction = Math.Clamp(progress.Fraction, 0, 1);
        await Show(State with
        {
            Started = [.. State.Started.Select(item => item.Id == progress.Operation.Value ? item with { Fraction = fraction } : item)],
        });
    }

    [On]
    public async Task On(OperationDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        if (!State.Started.Any(item => item.Id == done.Operation.Value && !item.Done)) { return; }
        var next = State with
        {
            Files = State.Files with { Revision = checked(State.Files.Revision + 1) },
            Started = [.. State.Started.Select(item => item.Id == done.Operation.Value
                ? item with { Done = true, Fraction = 1, Status = StatusOf(item, done.Result), Failed = done.Result is OperationResult.Failed }
                : item)],
        };
        if (next.Files.Connection is { } connection)
        {
            try
            {
                var app = new FilesApp(broker is CapabilityBroker runtime ? runtime.ForApp(AppKey) : broker, Id.Brain, next.Files);
                await app.Browse(new(connection, next.Files.Folder));
                next = next with { Files = app.Snapshot with { Revision = next.Files.Revision }, PageOffset = 0 };
            }
            catch (StorageException error) { next = next with { Notice = "Storage: " + error.Code }; }
        }
        await Show(next);
        await Context.Wires.Remove(new NeuronId(Id.Brain, "runtime.operation", done.Operation.Value));
    }

    internal static string StatusOf(FilesStartedOperation operation, OperationResult result) => result switch
    {
        OperationResult.Succeeded => operation.Label == "Move to trash" ? "Moved to trash" : "Completed",
        OperationResult.Cancelled => "Cancelled",
        OperationResult.Planned => "Dry run complete. No files changed.",
        OperationResult.Failed failed => "Failed: " + failed.Code,
        _ => "Finished",
    };

    // The selected file's text when the person may edit it here: a text preview in a writable folder that a field can hold.
    internal static string? Editable(FilesState files)
        => files.Selected is { Item.Kind: ItemKind.File } selected
            && FilesApp.IsText(selected.Item.Name)
            && files.Breadcrumbs.LastOrDefault()?.Item.Flags.HasFlag(ItemFlags.Write) == true
            && files.Preview is { } text
            && !string.Equals(text, FilesCopy.PreviewUnavailable, StringComparison.Ordinal)
            && text.Length <= MaxEditableText
            && files.PreviewNote is null
                ? text
                : null;

    // Ino gets the prompt as a request; Files does not wait for it and keeps the prompt on screen either way.
    private async Task HandToIno(FilesState files, string prompt)
    {
        var preview = files.Preview;
        var enriched = !string.IsNullOrWhiteSpace(preview) && !string.Equals(preview, FilesCopy.PreviewUnavailable, StringComparison.Ordinal)
            ? $"{prompt}\n\n[File Preview]\n{(preview.Length <= 1200 ? preview : preview[..1200])}"
            : prompt;
        try
        {
            await Get<IApp>(PlainNeuronContracts.IdOf(typeof(IApp), IntoChat.OS.Host.Primitives.AppRef.Of(Id.Brain, ShippedApps.Ino).Key)).Request("send", enriched);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogDebug(error, "The Ino handoff was not delivered.");
        }
    }

    private async Task FollowOperation(string operation, CancellationToken ct = default)
    {
        var source = new NeuronId(Id.Brain, "runtime.operation", operation);
        // Ensure starts a fresh cursor at the tail. Read durable history after subscribing so a fast completion cannot fall
        // between Start and Watch; a later delivery is harmless because a completed operation is applied once.
        await Context.Wires.Ensure(source, typeof(OperationProgress), typeof(OperationDone));
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

    private static T At<T>(T[] items, int? index) => index is >= 0 && index < items.Length ? items[index.Value] : throw new StaleActionException("This item is no longer available.");
}
