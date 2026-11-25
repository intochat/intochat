/*
# Files
Role: Default
Shipped: true
Operations: open, browse, select, search, more, preview

Files browses and searches every connected storage through storage@1. The same app prepares create, write, rename, copy,
move and trash plans for the runtime operation and confirmation gate. Storage IDs are opaque and connection-bound.

## Scenario: Opening shows connected storages
Invoking "open" lists the connected storages and their available actions, or explains how to connect one in Settings.
## Scenario: Browsing and paging retain every item
Invoking "browse" lists a folder with provider breadcrumbs. Invoking "more" adds the next page without dropping items.
## Scenario: Search keeps each result attached to its storage
Invoking "search" finds names recursively in the current folder or across all storages. Invoking "select" selects the exact item.
## Scenario: Preview reads bounded text
Invoking "preview" opens folders or previews at most 256 KiB of a file, with an explicit limit and a binary-file state.
## Scenario: Writes use safe defaults
Prepared plans fail on conflicting names and send deletes to trash. Read-only targets cannot produce a write plan.
Cross-storage moves preserve both the source connection and destination; dry run and replacement require explicit choices.
*/
using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;

namespace IntoChat.OS.Modules.Apps;

internal sealed record FilesStorage(string Connection, string Label, string Kind, ItemFlags Flags, string? Error, Quota? Space = null);
internal sealed record FilesGroup(string Connection, string Label, FilesItem[] Items, bool Failed, bool Bounded, string? Cursor = null);
internal sealed record FilesItem(string Connection, Item Item);
internal sealed record FilesPage(string Connection, ItemId? Folder, string? Cursor, bool Complete);
internal sealed record FilesClipboard(FilesItem Source, bool Move);
[Signal("files.opened")] internal sealed record FilesOpened : Signal;
[Signal("files.more")] internal sealed record FilesMore : Signal;
[Signal("files.preview")] internal sealed record FilesPreview : Signal;
[Signal("files.location")] internal sealed record FilesLocation(string Connection, ItemId? Folder = null) : Signal;
[Signal("files.search")] internal sealed record FilesSearch(string Text, bool Everywhere = false) : Signal;
[Signal("files.selected")] internal sealed record FilesSelected(FilesItem Item) : Signal;
internal sealed record FilesState
{
    public long Revision { get; init; }
    public FilesStorage[] Storages { get; init; } = [];
    public string? Connection { get; init; }
    public ItemId? Folder { get; init; }
    public FilesItem[] Items { get; init; } = [];
    public FilesItem[] Breadcrumbs { get; init; } = [];
    public FilesItem? Selected { get; init; }
    public string? SelectedNeuronUri { get; init; }
    public string? InoHandoffPrompt { get; init; }
    public FilesClipboard? Clipboard { get; init; }
    public FilesPage[] Pages { get; init; } = [];
    public string? Search { get; init; }
    public string? Preview { get; init; }
    public string? PreviewUrl { get; init; }
    public string? PreviewApp { get; init; }
    public string? PreviewNote { get; init; }
    public string? DownloadUrl { get; init; }
    public string? Message { get; init; }
    public string? StatusTitle { get; init; }
    public string? StatusBody { get; init; }
    public string StatusSeverity { get; init; } = "info";
    public string Filter { get; init; } = "";
    public string Sort { get; init; } = "name";
    public bool SortDescending { get; init; }
    public bool Loading { get; init; }
    public bool Searching { get; init; }
    public FilesGroup[] Groups { get; init; } = [];
    public string? PreviousConnection { get; init; }
    public ItemId? PreviousFolder { get; init; }
    public ConflictPolicy Conflict { get; init; }
    public bool DryRun { get; init; }
}

// Files uses storage@1 only. The host persists snapshots and submits plans to the runtime's confirmation/operation gate.
internal sealed class FilesApp(ICapabilityBroker broker, BrainId brain, FilesState state, IReadOnlyList<IStorage>? storages = null)
{
    public const string AppName = "files";
    public const int PageSize = 100;
    public static readonly TimeSpan DefaultSearchLimit = TimeSpan.FromSeconds(2);
    public IReadOnlyList<IStorage>? Storages { get; } = storages;
    public FilesState Snapshot { get; private set; } = state;
    public TimeSpan SearchLimit { get; set; } = DefaultSearchLimit;
    public IReadOnlySet<string> InstalledApps { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;

    public async Task Open(FilesOpened request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var storages = new List<FilesStorage>();
        var openRoots = new List<FilesItem>();
        foreach (var connection in await broker.ResolveAll<IStorage>(brain, ct))
        {
            try
            {
                var descriptor = await connection.Capability.Describe(ct);
                var roots = await connection.Capability.Roots(ct);
                storages.Add(new(connection.ConnectionKey, connection.Label, descriptor.Kind, descriptor.Flags, null, descriptor.Quota));
                openRoots.AddRange(roots.Select(root => new FilesItem(connection.ConnectionKey, root)));
            }
            catch (StorageException error)
            {
                storages.Add(new(connection.ConnectionKey, connection.Label, "storage", ItemFlags.None, error.Code));
            }
        }
        var empty = storages.Count == 0;
        Set(ClearSelection(Snapshot) with
        {
            Storages = [.. storages],
            Connection = null,
            Folder = null,
            Items = [.. openRoots],
            Breadcrumbs = [],
            Pages = [],
            Search = null,
            Searching = false,
            Groups = [],
            Loading = false,
            StatusTitle = empty ? FilesCopy.NoStorageHeading : null,
            StatusBody = empty ? FilesCopy.NoStorageBody : null,
            StatusSeverity = "info",
        });
    }

    public async Task Browse(FilesLocation location, CancellationToken ct = default)
    {
        var (connection, folder) = location;
        var storage = await Storage(connection, ct);
        Set(Snapshot with { Loading = true, Searching = false, StatusTitle = null, StatusBody = null, Connection = connection, Folder = folder });
        try
        {
            Item[] items;
            Item[] ancestors = [];
            FilesPage[] pages = [];
            if (folder is { } id)
            {
                var current = await storage.Stat(id, ct);
                if (current is { Kind: ItemKind.Link, Target: { } target }) { current = await storage.Stat(target, ct); id = current.Id; folder = id; }
                if (current.Kind != ItemKind.Folder) { throw new Unsupported(id); }
                ancestors = [.. await storage.Ancestors(id, ct), current];
                var page = await storage.List(id, new(PageSize: PageSize), ct);
                items = [.. page.Items];
                pages = [new(connection, id, page.NextCursor, page.NextCursor is null)];
            }
            else { items = [.. await storage.Roots(ct)]; }
            Set(ClearSelection(Snapshot) with
            {
                Loading = false,
                Connection = connection,
                Folder = folder,
                Items = [.. items.Select(item => new FilesItem(connection, item))],
                Breadcrumbs = [.. ancestors.Select(item => new FilesItem(connection, item))],
                Pages = pages,
                Search = null,
                Groups = [],
                StatusTitle = null,
                StatusBody = null,
            });
        }
        catch (StorageException error)
        {
            var (title, body, severity) = Explain(error, LabelOf(connection));
            Set(Snapshot with { Loading = false, StatusTitle = title, StatusBody = body, StatusSeverity = severity, Items = [], Message = null });
        }
    }

    public async Task Select(FilesSelected request, CancellationToken ct = default)
    {
        var selected = request.Item;
        var item = await (await Storage(selected.Connection, ct)).Stat(selected.Item.Id, ct);
        var chosen = new FilesItem(selected.Connection, item);
        Set(ClearSelection(Snapshot) with
        {
            Selected = chosen,
            SelectedNeuronUri = NeuronUriOf(chosen),
            DownloadUrl = item.Kind == ItemKind.File ? ContentUrl(selected.Connection, item.Id) : null,
        });
        if (item.Kind == ItemKind.File) { await LoadPreview(ct); }
    }

    public async Task Search(FilesSearch query, CancellationToken ct = default)
    {
        var (text, everywhere) = query;
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var needle = text.Trim();
        var connections = (await broker.ResolveAll<IStorage>(brain, ct))
            .Where(item => everywhere || Snapshot.Connection is null || item.ConnectionKey == Snapshot.Connection)
            .ToArray();
        var within = everywhere ? null : Snapshot.Folder;
        Set(Snapshot with { Loading = true, Searching = true, Search = needle, StatusTitle = null, StatusBody = null });
        var found = await Task.WhenAll(connections.Select(connection => SearchOne(connection, needle, within, ct)));
        var items = found.SelectMany(group => group.Items).ToArray();
        var failed = found.Where(group => group.Failed).Select(group => group.Label).ToArray();
        var responded = found.Where(group => !group.Failed).ToArray();
        string? title = null, body = null;
        var severity = "info";
        if (failed.Length > 0)
        {
            title = FilesCopy.SearchMissed;
            body = FilesCopy.SearchMissedBody(string.Join(", ", failed));
            severity = "warning";
        }
        else if (responded.Length > 0 && items.Length == 0 && found.All(group => group.Items.Length == 0 && !group.Bounded))
        {
            title = FilesCopy.NothingNamed(needle);
        }
        Set(ClearSelection(Snapshot) with
        {
            Loading = false,
            Searching = true,
            Items = items,
            Groups = found,
            Pages = [.. found.Select(group => new FilesPage(group.Connection, within, group.Cursor, !group.Bounded || group.Failed))],
            Search = needle,
            DownloadUrl = Snapshot.DownloadUrl,
            StatusTitle = title,
            StatusBody = body,
            StatusSeverity = severity,
        });
    }

    public async Task More(FilesMore request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var items = Snapshot.Items.ToList();
        var pages = Snapshot.Pages.ToArray();
        for (var n = 0; n < pages.Length; n++)
        {
            var cursor = pages[n];
            if (cursor.Complete) { continue; }
            var storage = await Storage(cursor.Connection, ct);
            var page = Snapshot.Search is { } search
                ? await storage.Search(new(search, Within: cursor.Folder, Cursor: cursor.Cursor, PageSize: PageSize), ct)
                : await storage.List(cursor.Folder!.Value, new(cursor.Cursor, PageSize), ct);
            items.AddRange(page.Items.Select(item => new FilesItem(cursor.Connection, item)));
            pages[n] = cursor with { Cursor = page.NextCursor, Complete = page.NextCursor is null };
        }
        var distinct = items.DistinctBy(item => (item.Connection, item.Item.Id)).ToArray();
        Set(Snapshot with
        {
            Items = distinct,
            Pages = pages,
            Loading = false,
            Message = null,
            StatusTitle = distinct.Length > 0 && Snapshot.StatusTitle == FilesCopy.NothingNamed(Snapshot.Search ?? "") ? null : Snapshot.StatusTitle,
        });
    }

    public async Task Preview(FilesPreview request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Snapshot.Selected is null) { throw new InvalidOperationException("Select a file first."); }
        if (Snapshot.Selected.Item.Kind == ItemKind.Folder)
        {
            await Browse(new(Snapshot.Selected.Connection, Snapshot.Selected.Item.Id), ct);
            return;
        }
        await LoadPreview(ct);
    }

    public void Filter(string? text) => Set(Snapshot with { Filter = text?.Trim() ?? "" });

    public void SortBy(string column)
    {
        var next = column is "size" or "modified" or "name" ? column : "name";
        Set(Snapshot with { Sort = next, SortDescending = Snapshot.Sort == next && !Snapshot.SortDescending });
    }

    public void ShowUnavailable(string connection)
    {
        var storage = Snapshot.Storages.Single(item => item.Connection == connection);
        Set(ClearSelection(Snapshot) with
        {
            Connection = connection,
            Folder = null,
            Items = [],
            Breadcrumbs = [],
            Pages = [],
            Searching = false,
            Loading = false,
            DownloadUrl = Snapshot.DownloadUrl,
            StatusTitle = FilesCopy.UnavailableTitle(storage.Label),
            StatusBody = FilesCopy.UnavailableBody,
            StatusSeverity = "warning",
        });
    }

    public async Task OpenResult(FilesItem item, CancellationToken ct = default)
    {
        var (backConnection, backFolder) = (Snapshot.Connection, Snapshot.Folder);
        var storage = await Storage(item.Connection, ct);
        var stat = await storage.Stat(item.Item.Id, ct);
        if (stat is { Kind: ItemKind.Link, Target: { } target }) { stat = await storage.Stat(target, ct); }
        var ancestors = await storage.Ancestors(stat.Id, ct);
        var folder = stat.Kind == ItemKind.Folder ? stat.Id : ancestors.Count == 0 ? (ItemId?)null : ancestors[^1].Id;
        await Browse(new(item.Connection, folder), ct);
        var chosen = Snapshot.Items.FirstOrDefault(entry => entry.Item.Id == stat.Id) ?? new FilesItem(item.Connection, stat);
        Set(Snapshot with
        {
            Selected = chosen,
            SelectedNeuronUri = NeuronUriOf(chosen, ancestors),
            Searching = false,
            PreviousConnection = backConnection,
            PreviousFolder = backFolder,
            DownloadUrl = stat.Kind == ItemKind.File ? ContentUrl(item.Connection, stat.Id) : null,
        });
        if (stat.Kind == ItemKind.File) { await LoadPreview(ct); }
    }

    public async Task BackToFolder(CancellationToken ct = default)
    {
        if (Snapshot.PreviousConnection is { } connection) { await Browse(new(connection, Snapshot.PreviousFolder), ct); }
    }

    public void Copy(bool move)
    {
        var selected = Snapshot.Selected ?? throw new InvalidOperationException("Select a file or folder first.");
        Require(selected.Item.Flags, move ? ItemFlags.Move : ItemFlags.Copy, selected.Item.Id);
        Set(Snapshot with { Clipboard = new(selected, move), Message = $"Choose a destination folder, then {(move ? "Move here" : "Copy here")}." });
    }

    public string AskIno()
    {
        var selected = Snapshot.Selected ?? Snapshot.Breadcrumbs.LastOrDefault() ?? Snapshot.Items.FirstOrDefault() ?? throw new InvalidOperationException("Select a file or folder first.");
        var uri = Snapshot.SelectedNeuronUri ?? NeuronUriOf(selected);
        var prompt = $"Explain and summarize {selected.Item.Name} ({uri})";
        Set(Snapshot with { SelectedNeuronUri = uri, InoHandoffPrompt = prompt, Message = $"Opened Ino with {uri}" });
        return prompt;
    }

    public void Options(ConflictPolicy conflict, bool dryRun) => Set(Snapshot with { Conflict = conflict, DryRun = dryRun });

    public WritePlan Plan(string operation, string name = "", string text = "")
    {
        var selected = Snapshot.Selected;
        var folder = Snapshot.Folder;
        WritePlan plan = operation switch
        {
            "mkdir" when folder is { } parent => WritePlan.CreateFolder(parent, name, Snapshot.Conflict),
            "write" when folder is { } parent => WritePlan.WriteFile(parent, name, Encoding.UTF8.GetBytes(text), Snapshot.Conflict),
            "rename" when selected is not null => WritePlan.Rename(selected.Item.Id, name, Snapshot.Conflict),
            "delete" when selected is not null => WritePlan.Delete(selected.Item.Id),
            "paste" when folder is { } parent && Snapshot.Clipboard is { } clip => clip.Move
                ? WritePlan.Move(clip.Source.Item.Id, parent, Snapshot.Conflict) : WritePlan.Copy(clip.Source.Item.Id, parent, Snapshot.Conflict),
            _ => throw new InvalidOperationException("Select the source and a destination folder before this action."),
        };
        if (operation is "mkdir" or "write" or "paste")
        {
            var current = Snapshot.Breadcrumbs.LastOrDefault()?.Item ?? throw new InvalidOperationException("Open a destination folder first.");
            Require(current.Flags, operation == "mkdir" ? ItemFlags.CreateFolder : ItemFlags.Write, current.Id);
        }
        if (operation == "rename") { Require(selected!.Item.Flags, ItemFlags.Rename, selected.Item.Id); }
        if (operation == "delete") { Require(selected!.Item.Flags, ItemFlags.Trash, selected.Item.Id); }
        return plan with { DryRun = Snapshot.DryRun };
    }

    private async Task<FilesGroup> SearchOne(CapabilityConnection<IStorage> connection, string text, ItemId? within, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SearchLimit);
        try
        {
            var page = await connection.Capability.Search(new(text, Within: within, PageSize: PageSize), timeout.Token).ConfigureAwait(false);
            var rows = page.Items.Select(item => new FilesItem(connection.ConnectionKey, item)).ToArray();
            return new(connection.ConnectionKey, connection.Label, rows, false, page.NextCursor is not null, page.NextCursor);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(connection.ConnectionKey, connection.Label, [], true, false); }
        catch (StorageException) { return new(connection.ConnectionKey, connection.Label, [], true, false); }
    }

    private async Task LoadPreview(CancellationToken ct)
    {
        var selected = Snapshot.Selected ?? throw new InvalidOperationException("Select a file first.");
        var storage = await Storage(selected.Connection, ct).ConfigureAwait(false);
        var item = await storage.Stat(selected.Item.Id, ct).ConfigureAwait(false);
        if (item is { Kind: ItemKind.Link, Target: { } target }) { item = await storage.Stat(target, ct).ConfigureAwait(false); }
        if (item.Kind == ItemKind.Folder) { await Browse(new(selected.Connection, item.Id), ct).ConfigureAwait(false); return; }
        var url = ContentUrl(selected.Connection, item.Id);
        var ancestors = await storage.Ancestors(item.Id, ct).ConfigureAwait(false);
        var app = ancestors.LastOrDefault(folder => InstalledApps.Contains(folder.Name));
        if (item.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && app is { } match)
        {
            Set(Snapshot with { Preview = null, PreviewUrl = null, PreviewApp = match.Name, PreviewNote = null, DownloadUrl = url, Message = null });
            return;
        }
        if (IsImage(item.Name))
        {
            Set(item.Size is > TenMegabytes
                ? Snapshot with { Preview = FilesCopy.PreviewTooLarge, PreviewUrl = null, PreviewApp = null, PreviewNote = null, DownloadUrl = url }
                : Snapshot with { Preview = null, PreviewUrl = url, PreviewApp = null, PreviewNote = null, DownloadUrl = url, Message = null });
            return;
        }
        if (IsAudio(item.Name) || IsVideo(item.Name) || IsPdf(item.Name))
        {
            Set(Snapshot with { Preview = null, PreviewUrl = url, PreviewApp = null, PreviewNote = null, DownloadUrl = url, Message = null });
            return;
        }
        if (!IsText(item.Name))
        {
            Set(Snapshot with { Preview = FilesCopy.PreviewUnavailable, PreviewUrl = null, PreviewApp = null, PreviewNote = null, DownloadUrl = url });
            return;
        }
        var content = await storage.Read(item.Id, new ByteRange(0, 256 * 1024), ct).ConfigureAwait(false);
        if (content.Bytes.Span.Contains((byte)0))
        {
            Set(Snapshot with { Preview = FilesCopy.PreviewUnavailable, PreviewUrl = null, PreviewApp = null, PreviewNote = null, DownloadUrl = url });
            return;
        }
        Set(Snapshot with
        {
            Preview = Encoding.UTF8.GetString(content.Bytes.Span),
            PreviewNote = content.TotalLength > content.Bytes.Length ? FilesCopy.PreviewLimited : null,
            PreviewUrl = null,
            PreviewApp = null,
            DownloadUrl = url,
            Message = null,
        });
    }

    private static FilesState ClearSelection(FilesState s) => s with
    {
        Selected = null,
        SelectedNeuronUri = null,
        Preview = null,
        PreviewUrl = null,
        PreviewApp = null,
        PreviewNote = null,
        DownloadUrl = null,
        Message = null,
    };

    private void Set(FilesState next)
    {
        Snapshot = next;
        Changed?.Invoke();
    }

    private string? LabelOf(string connection) => Snapshot.Storages.FirstOrDefault(item => item.Connection == connection)?.Label;

    public string NeuronUriOf(FilesItem item, IReadOnlyList<Item>? ancestors = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var chain = ancestors ?? Snapshot.Breadcrumbs.Where(crumb => crumb.Connection == item.Connection).Select(crumb => crumb.Item).ToArray();
        var segments = chain.Skip(1).Select(folder => SlugSegment(folder.Name)).Append(SlugSegment(item.Item.Name)).Where(segment => segment.Length > 0);
        return $"neuron://{brain.Value}/{SlugSegment(item.Connection)}.files/{string.Join("/", segments)}";
    }

    private static string SlugSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_') { builder.Append(ch); }
            else if (char.IsWhiteSpace(ch) || ch is '/' or '\\') { builder.Append('-'); }
        }
        return builder.ToString().Trim('-');
    }

    // The hosted content route (StorageContentHosting) in its one key form.
    private string ContentUrl(string connection, ItemId item) => DigitalBrain.Runtime.Http.Storage.StorageContentEndpoints.ContentPath(brain.Value, connection, item.Value);

    private async Task<IStorage> Storage(string connection, CancellationToken ct)
        => (await broker.ResolveAll<IStorage>(brain, ct).ConfigureAwait(false)).SingleOrDefault(item => item.ConnectionKey == connection)?.Capability
            ?? throw new Unavailable(null);

    private static (string Title, string Body, string Severity) Explain(StorageException error, string? label) => error switch
    {
        Unavailable when label is not null => (FilesCopy.UnavailableTitle(label), FilesCopy.UnavailableBody, "warning"),
        Denied => (FilesCopy.DeniedTitle, FilesCopy.DeniedBody, "error"),
        _ => (FilesCopy.ListFailed, FilesCopy.NothingChanged, "error"),
    };

    internal static bool IsImage(string name) => HasExtension(name, ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp", ".ico", ".avif", ".tiff", ".tif");
    internal static bool IsAudio(string name) => HasExtension(name, ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma");
    internal static bool IsVideo(string name) => HasExtension(name, ".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v");
    internal static bool IsPdf(string name) => HasExtension(name, ".pdf");
    internal static bool IsMarkdown(string name) => HasExtension(name, ".md", ".markdown");
    internal static bool IsCode(string name) => HasExtension(name, ".cs", ".dart", ".ts", ".js", ".py", ".json", ".yaml", ".yml", ".xml", ".html", ".htm", ".css", ".scss", ".sql", ".sh", ".ps1", ".bat", ".rs", ".go", ".java", ".cpp", ".c", ".h");
    internal static bool IsText(string name) => IsMarkdown(name) || IsCode(name) || HasExtension(name, ".txt", ".csv", ".tsv", ".log", ".env", ".ini", ".conf");

    private static bool HasExtension(string name, params string[] extensions)
    {
        var ext = Path.GetExtension(name);
        return extensions.Any(candidate => ext.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private const long TenMegabytes = 10L * 1024 * 1024;

    private static void Require(ItemFlags actual, ItemFlags required, ItemId item)
    { if (!actual.HasFlag(required)) { throw new Unsupported(item); } }
}
