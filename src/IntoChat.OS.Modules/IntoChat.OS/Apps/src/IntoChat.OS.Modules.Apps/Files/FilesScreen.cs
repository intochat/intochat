using DigitalBrain.Capabilities.Storage;
using IntoChat.Files;
using IntoChat.OS.Modules.Flutter.Collection;
using IntoChat.OS.Modules.Flutter.Views;

namespace IntoChat.OS.Modules.Apps;

// The value tree the Files shell already renders: side list, toolbar, collection, preview.
internal static class FilesScreen
{
    internal sealed record Model(UiNode Side, UiNode Main, CollectionDefinition Files, string? ImageUrl);

    public static Model Compose(FilesState state, int width, bool previewOpen, long revision = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        var narrow = width <= 390;
        string Act(string verb, int? index = null) => revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + verb
            + (index is { } value ? ":" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
        if (state.Storages.Length == 0 && state.Connection is null && !state.Loading && !state.Searching)
        {
            var empty = Ui.Column("files-empty",
            [
                Ui.Text("no-storage-heading", FilesCopy.NoStorageHeading),
                Ui.Text("no-storage-body", FilesCopy.NoStorageBody),
                Ui.Button("open-settings", FilesCopy.OpenSettings, Act("open-settings")),
            ]);
            return new(Ui.Column("files-side", [Ui.Text("storages-title", FilesCopy.StorageHeading)]), empty, new([], Error: null), null);
        }

        var sideRows = state.Storages.Select((storage, index) =>
        {
            var quota = storage.Error is null ? QuotaText.For(storage.Space ?? Quota.Unknown, storage.Kind) : FilesCopy.Unavailable;
            return Ui.Card("storage-" + index, storage.Label, quota,
                [Ui.Button("storage-open-" + index, storage.Error is null ? "Browse" : FilesCopy.TryAgain, Act(storage.Error is null ? "storage" : "retry", index))]);
        }).ToArray();
        var side = Ui.Column("files-side", [Ui.Text("storages-title", FilesCopy.StorageHeading), .. sideRows]);

        var selected = state.Selected;
        var folder = state.Breadcrumbs.LastOrDefault()?.Item.Flags ?? ItemFlags.None;
        var writable = folder.HasFlag(ItemFlags.Write);
        var create = folder.HasFlag(ItemFlags.CreateFolder);
        var one = selected is not null;
        var file = selected?.Item.Kind == ItemKind.File;
        UiNode Button(string key, string text, string verb, bool enabled, UiStyle style = UiStyle.Secondary, int? index = null)
            => new(UiNodeKind.Button, key) { Text = text, Action = Act(verb, index), Enabled = enabled, Style = style };
        var toolbar = Ui.Row("toolbar",
        [
            Button("upload", FilesCopy.Upload, "upload", writable, UiStyle.Primary),
            Button("mkdir", FilesCopy.NewFolder, "mkdir", create),
            Button("rename", FilesCopy.Rename, "rename", one && selected!.Item.Flags.HasFlag(ItemFlags.Rename)),
            Button("move", FilesCopy.Move, "move", one && selected!.Item.Flags.HasFlag(ItemFlags.Move)),
            Button("copy", FilesCopy.Copy, "copy", one && selected!.Item.Flags.HasFlag(ItemFlags.Copy)),
            Button("download", FilesCopy.Download, "download", file && selected!.Item.Flags.HasFlag(ItemFlags.Read), UiStyle.Link),
            Button("delete", FilesCopy.Delete, "delete", one && selected!.Item.Flags.HasFlag(ItemFlags.Trash)),
        ]);

        var files = state.Searching ? SearchCollection(state) : FolderCollection(state);
        var headers = Ui.Row("columns",
        [
            Button("sort-name", FilesCopy.NameHeader, "sort-name", true, state.Sort == "name" ? UiStyle.Primary : UiStyle.Ghost),
            Button("sort-size", FilesCopy.SizeHeader, "sort-size", true, state.Sort == "size" ? UiStyle.Primary : UiStyle.Ghost),
            Button("sort-modified", FilesCopy.ModifiedHeader, "sort-modified", true, state.Sort == "modified" ? UiStyle.Primary : UiStyle.Ghost),
        ]);
        var notices = new List<UiNode>();
        if (state.Loading)
        {
            notices.Add(Ui.Text("loading", FilesCopy.Loading));
        }
        if (state.StatusTitle is { } title)
        {
            notices.Add(Ui.Text("status-title", title));
            if (state.StatusBody is { } body) { notices.Add(Ui.Text("status-body", body)); }
            if (state.StatusSeverity == "warning") { notices.Add(Ui.Button("try-again", FilesCopy.TryAgain, Act("retry"))); }
        }
        if (!state.Searching && state.Filter.Length > 0 && files.Items.Length == 0 && !state.Loading && state.StatusTitle is null)
        {
            notices.Add(Ui.Text("filter-body", FilesCopy.NoMatchBody(state.Filter)));
            notices.Add(Ui.Button("clear-filter", FilesCopy.ClearFilter, Act("clear-filter")));
        }
        if (!state.Searching && state.Items.Length == 0 && state.Filter.Length == 0 && !state.Loading && state.StatusTitle is null && state.Connection is not null)
        {
            notices.Add(Ui.Text("empty-body", FilesCopy.EmptyBody));
        }
        foreach (var group in state.Searching ? state.Groups.Where(group => group.Bounded && !group.Failed) : [])
        {
            notices.Add(Ui.Text("bounded-" + group.Connection, FilesCopy.Bounded(group.Items.Length)));
        }
        if (!state.Searching && state.PreviousConnection is not null)
        {
            notices.Add(Ui.Button("back-folder", FilesCopy.BackToFolder, Act("back-folder")));
        }
        if (state.Pages.Any(page => !page.Complete))
        {
            notices.Add(Ui.Button("more", FilesCopy.LoadMore, Act("more")));
        }
        if (state.DownloadUrl is { } download)
        {
            notices.Add(Ui.Text("download-url", download));
        }

        var preview = Preview(state);
        var list = Ui.Column("files-list", [headers, .. notices, Ui.Reference("items", new(IntoChat.OS.Modules.Flutter.UIVocabulary.CollectionType, "items"))]);
        var chrome = new UiNode[]
        {
            Ui.Row("navigation", [Button("up", FilesCopy.Up, "up", state.Connection is not null)]),
            Crumbs(state),
            toolbar,
            Ui.Reference("filter", new(IntoChat.OS.Modules.Flutter.UIVocabulary.TextFieldType, "filter")),
            Ui.Reference("search", new(IntoChat.OS.Modules.Flutter.UIVocabulary.TextFieldType, "search")),
        };
        var main = narrow && previewOpen && preview is not null
            ? Ui.Column("files-main", [Ui.Button("back-files", FilesCopy.BackToFiles, Act("back-files")), preview])
            : Ui.Column("files-main", [.. chrome, preview is null ? list : Ui.Row("browse-split", [list, preview], extents: [1, 360])]);
        return new(side, main, files, state.PreviewUrl);
    }

    public static IReadOnlyList<FilesItem> Ordered(FilesState state)
    {
        var source = state.Items.Where(item => state.Filter.Length == 0 || item.Item.Name.Contains(state.Filter, StringComparison.OrdinalIgnoreCase));
        return [.. Order(source, state.Sort, state.SortDescending)];
    }

    private static CollectionDefinition FolderCollection(FilesState state)
    {
        if (state.Loading || state.StatusTitle is not null)
        {
            return new([], Error: state.Loading ? null : state.StatusTitle, EmptyLabel: null);
        }
        var rows = Ordered(state).Select(Row).ToArray();
        var empty = state.Filter.Length > 0 ? FilesCopy.NoMatchHeading : state.Items.Length == 0 ? FilesCopy.EmptyHeading : null;
        var selection = state.Selected is { } selected ? RowId(selected) : null;
        return new(rows, Selection: selection, Error: null, FileMode: true, EmptyLabel: rows.Length == 0 ? empty : null,
            Breadcrumbs: state.Breadcrumbs.Select(item => new CollectionBreadcrumb(item.Item.Id.Value, item.Item.Name)).ToArray());
    }

    private static CollectionDefinition SearchCollection(FilesState state)
    {
        var items = new List<CollectionItem>();
        foreach (var group in state.Groups)
        {
            items.Add(new("group:" + group.Connection, group.Label, "group", CanActivate: false));
            if (group.Failed)
            {
                items.Add(new("notice:" + group.Connection, FilesCopy.GroupSilent(group.Label), "group", CanActivate: false));
            }
            else if (group.Items.Length == 0)
            {
                items.Add(new("empty:" + group.Connection, FilesCopy.NoMatchesIn(group.Label), "group", CanActivate: false));
            }
            else
            {
                items.AddRange(group.Items.Select(Row));
            }
        }
        return new([.. items], Error: null, FileMode: true, EmptyLabel: null);
    }

    private static CollectionItem Row(FilesItem entry)
    {
        var folder = entry.Item.Kind == ItemKind.Folder;
        var kind = folder ? "folder" : Path.GetExtension(entry.Item.Name).TrimStart('.').ToLowerInvariant();
        if (kind.Length == 0) { kind = "file"; }
        return new(RowId(entry), entry.Item.Name, kind, SizeBytes: folder ? null : entry.Item.Size, ModifiedAt: entry.Item.Modified,
            TrailingText: folder ? "" : null);
    }

    private static string RowId(FilesItem entry) => entry.Connection + "/" + entry.Item.Id.Value;

    private static UiNode Crumbs(FilesState state) => Ui.Row("breadcrumbs", state.Breadcrumbs.Select((item, index) =>
        Ui.Button("crumb-" + index, item.Item.Name, state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":breadcrumb:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray());

    private static UiNode? Preview(FilesState state)
    {
        if (state.PreviewApp is { } app)
        {
            return Ui.Card("preview-app", app, null, [Ui.Button("open-app", FilesCopy.OpenApp, state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":open-app")]);
        }
        if (state.PreviewUrl is { } url)
        {
            return Ui.Column("preview", [Ui.Text("preview-url", url), Ui.Reference("preview-image", new(IntoChat.OS.Modules.Flutter.UIVocabulary.ImageType, "preview"))]);
        }
        if (state.Preview is { } text)
        {
            var children = new List<UiNode> { Ui.Text("preview-text", text) };
            if (state.PreviewNote is { } note) { children.Add(Ui.Text("preview-note", note)); }
            return Ui.Column("preview", [.. children]);
        }
        return null;
    }

    private static IEnumerable<FilesItem> Order(IEnumerable<FilesItem> items, string sort, bool descending)
    {
        var list = items.ToArray();
        var folders = Sort(list.Where(item => item.Item.Kind == ItemKind.Folder), sort, descending);
        var files = Sort(list.Where(item => item.Item.Kind != ItemKind.Folder), sort, descending);
        return folders.Concat(files);
    }

    private static IEnumerable<FilesItem> Sort(IEnumerable<FilesItem> items, string sort, bool descending) => sort switch
    {
        "size" => descending ? items.OrderByDescending(item => item.Item.Size ?? -1) : items.OrderBy(item => item.Item.Size ?? -1),
        "modified" => descending
            ? items.OrderByDescending(item => item.Item.Modified ?? DateTimeOffset.MinValue)
            : items.OrderBy(item => item.Item.Modified ?? DateTimeOffset.MinValue),
        _ => descending
            ? items.OrderByDescending(item => item.Item.Name, StringComparer.OrdinalIgnoreCase)
            : items.OrderBy(item => item.Item.Name, StringComparer.OrdinalIgnoreCase),
    };
}
