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

namespace IntoChat.Files;

public sealed record FilesStorage(string Connection, string Label, string Kind, ItemFlags Flags, string? Error);
public sealed record FilesItem(string Connection, Item Item);
public sealed record FilesPage(string Connection, ItemId? Folder, string? Cursor, bool Complete);
public sealed record FilesClipboard(FilesItem Source, bool Move);
[Signal("files.opened")] public sealed record FilesOpened : Signal;
[Signal("files.more")] public sealed record FilesMore : Signal;
[Signal("files.preview")] public sealed record FilesPreview : Signal;
[Signal("files.location")] public sealed record FilesLocation(string Connection, ItemId? Folder = null) : Signal;
[Signal("files.search")] public sealed record FilesSearch(string Text, bool Everywhere = false) : Signal;
[Signal("files.selected")] public sealed record FilesSelected(FilesItem Item) : Signal;
public sealed record FilesState
{
    public long Revision { get; init; }
    public FilesStorage[] Storages { get; init; } = [];
    public string? Connection { get; init; }
    public ItemId? Folder { get; init; }
    public FilesItem[] Items { get; init; } = [];
    public FilesItem[] Breadcrumbs { get; init; } = [];
    public FilesItem? Selected { get; init; }
    public FilesClipboard? Clipboard { get; init; }
    public FilesPage[] Pages { get; init; } = [];
    public string? Search { get; init; }
    public string? Preview { get; init; }
    public string? Message { get; init; }
    public ConflictPolicy Conflict { get; init; }
    public bool DryRun { get; init; }
}

// Files uses storage@1 only. The host persists snapshots and submits plans to the runtime's confirmation/operation gate.
[App("files")]
public sealed class FilesApp(ICapabilityBroker broker, BrainId brain, FilesState state) : App
{
    public static AppManifest Manifest { get; } = new("files", "Files", "3.0.1", [], new([Capability.Storage.V1]));
    public FilesState Snapshot { get; private set; } = state;

    [On]
    public async Task Open(FilesOpened request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var storages = new List<FilesStorage>();
        foreach (var connection in await broker.ResolveAll<IStorage>(brain, ct))
        {
            try
            {
                var descriptor = await connection.Capability.Describe(ct);
                storages.Add(new(connection.ConnectionKey, connection.Label, descriptor.Kind, descriptor.Flags, null));
            }
            catch (StorageException error)
            { storages.Add(new(connection.ConnectionKey, connection.Label, "storage", ItemFlags.None, error.Code)); }
        }
        Snapshot = Snapshot with
        {
            Storages = [.. storages],
            Connection = null,
            Folder = null,
            Items = [],
            Breadcrumbs = [],
            Selected = null,
            Pages = [],
            Search = null,
            Preview = null,
            Message = storages.Count == 0 ? "No storage is connected. Add a storage connection in Settings." : null,
        };
    }

    [On]
    public async Task Browse(FilesLocation location, CancellationToken ct = default)
    {
        var (connection, folder) = location;
        var storage = await Storage(connection, ct);
        Item[] items;
        Item[] ancestors = [];
        FilesPage[] pages = [];
        if (folder is { } id)
        {
            var current = await storage.Stat(id, ct);
            if (current is { Kind: ItemKind.Link, Target: { } target }) { current = await storage.Stat(target, ct); id = current.Id; folder = id; }
            if (current.Kind != ItemKind.Folder) { throw new Unsupported(id); }
            ancestors = [.. await storage.Ancestors(id, ct), current];
            var page = await storage.List(id, new(PageSize: 40), ct);
            items = [.. page.Items];
            pages = [new(connection, id, page.NextCursor, page.NextCursor is null)];
        }
        else { items = [.. await storage.Roots(ct)]; }
        Snapshot = Snapshot with
        {
            Connection = connection,
            Folder = folder,
            Items = items.Select(item => new FilesItem(connection, item)).ToArray(),
            Breadcrumbs = ancestors.Select(item => new FilesItem(connection, item)).ToArray(),
            Selected = null,
            Pages = pages,
            Search = null,
            Preview = null,
            Message = items.Length == 0 ? "This folder is empty." : null,
        };
    }

    [On]
    public async Task Select(FilesSelected request, CancellationToken ct = default)
    {
        var selected = request.Item;
        var item = await (await Storage(selected.Connection, ct)).Stat(selected.Item.Id, ct);
        Snapshot = Snapshot with { Selected = new(selected.Connection, item), Preview = null, Message = null };
    }

    [On]
    public async Task Search(FilesSearch query, CancellationToken ct = default)
    {
        var (text, everywhere) = query;
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var connections = await broker.ResolveAll<IStorage>(brain, ct);
        var pages = new List<FilesPage>();
        var items = new List<FilesItem>();
        var unavailable = new List<string>();
        foreach (var connection in connections.Where(item => everywhere || Snapshot.Connection is null || item.ConnectionKey == Snapshot.Connection))
        {
            var within = everywhere ? null : Snapshot.Folder;
            try
            {
                var page = await connection.Capability.Search(new(text.Trim(), Within: within, PageSize: 40), ct);
                items.AddRange(page.Items.Select(item => new FilesItem(connection.ConnectionKey, item)));
                pages.Add(new(connection.ConnectionKey, within, page.NextCursor, page.NextCursor is null));
            }
            catch (StorageException error) { unavailable.Add($"{connection.Label}: {error.Code}"); }
        }
        Snapshot = Snapshot with
        {
            Items = [.. items],
            Pages = [.. pages],
            Search = text.Trim(),
            Selected = null,
            Preview = null,
            Message = unavailable.Count > 0 ? "Some storages could not be searched: " + string.Join(", ", unavailable)
                : items.Count == 0 ? "No matching files or folders on this page." : null,
        };
    }

    [On]
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
                ? await storage.Search(new(search, Within: cursor.Folder, Cursor: cursor.Cursor, PageSize: 40), ct)
                : await storage.List(cursor.Folder!.Value, new(cursor.Cursor, 40), ct);
            items.AddRange(page.Items.Select(item => new FilesItem(cursor.Connection, item)));
            pages[n] = cursor with { Cursor = page.NextCursor, Complete = page.NextCursor is null };
        }
        Snapshot = Snapshot with
        {
            Items = [.. items.DistinctBy(item => (item.Connection, item.Item.Id))],
            Pages = pages,
            Message = items.Count > 0 && Snapshot.Message == "No matching files or folders on this page." ? null : Snapshot.Message,
        };
    }

    [On]
    public async Task Preview(FilesPreview request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selected = Snapshot.Selected ?? throw new InvalidOperationException("Select a file first.");
        var storage = await Storage(selected.Connection, ct);
        var item = await storage.Stat(selected.Item.Id, ct);
        if (item is { Kind: ItemKind.Link, Target: { } target }) { item = await storage.Stat(target, ct); }
        if (item.Kind == ItemKind.Folder) { await Browse(new(selected.Connection, item.Id), ct); return; }
        var content = await storage.Read(item.Id, new ByteRange(0, 256 * 1024), ct);
        var bytes = content.Bytes.Span;
        var preview = bytes.Contains((byte)0) ? "This file cannot be previewed as text." : Encoding.UTF8.GetString(bytes);
        if (content.TotalLength > content.Bytes.Length) { preview += "\n\nPreview limited to the first 256 KiB."; }
        Snapshot = Snapshot with { Preview = preview, Message = null };
    }

    public void Copy(bool move)
    {
        var selected = Snapshot.Selected ?? throw new InvalidOperationException("Select a file or folder first.");
        Require(selected.Item.Flags, move ? ItemFlags.Move : ItemFlags.Copy, selected.Item.Id);
        Snapshot = Snapshot with { Clipboard = new(selected, move), Message = $"Choose a destination folder, then {(move ? "Move here" : "Copy here")}." };
    }

    public void Options(ConflictPolicy conflict, bool dryRun) => Snapshot = Snapshot with { Conflict = conflict, DryRun = dryRun };

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

    private async Task<IStorage> Storage(string connection, CancellationToken ct)
        => (await broker.ResolveAll<IStorage>(brain, ct)).SingleOrDefault(item => item.ConnectionKey == connection)?.Capability
            ?? throw new Unavailable(null);

    private static void Require(ItemFlags actual, ItemFlags required, ItemId item)
    { if (!actual.HasFlag(required)) { throw new Unsupported(item); } }
}
