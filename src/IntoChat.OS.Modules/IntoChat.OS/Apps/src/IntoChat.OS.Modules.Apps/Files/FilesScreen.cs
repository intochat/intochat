using System.Globalization;
using System.Text;
using DigitalBrain.Capabilities.Storage;
using IntoChat.Files;
using IntoChat.OS.Modules.Flutter.Collection;
using IntoChat.OS.Modules.Flutter.Views;

namespace IntoChat.OS.Modules.Apps;

// The value tree the Files shell renders: side list, WinUI 3 breadcrumb bar, toolbar, interactive item list, QuickView preview.
internal static class FilesScreen
{
    internal sealed record Model(UiNode Side, UiNode Main, CollectionDefinition Files, string? ImageUrl);

    public static Model Compose(
        FilesState state,
        int width,
        bool previewOpen,
        long revision = 0,
        string appKey = "owner/apps/files",
        int pageOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(state);
        var narrow = width <= 390;
        var rev = (revision != 0 ? revision : state.Revision).ToString(CultureInfo.InvariantCulture);
        string Act(string verb, int? index = null) => rev + ":" + verb
            + (index is { } value ? ":" + value.ToString(CultureInfo.InvariantCulture) : "");
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
            var isCurrentStorage = string.Equals(state.Connection, storage.Connection, StringComparison.Ordinal);
            return Ui.Card("storage-" + index, storage.Label, quota,
                [Ui.Button(
                    "storage-open-" + index,
                    storage.Error is null ? "Browse" : FilesCopy.TryAgain,
                    Act(storage.Error is null ? "storage" : "retry", index),
                    style: isCurrentStorage ? UiStyle.Primary : UiStyle.Secondary,
                    icon: storage.Kind == "google-drive" ? "cloud" : "hard-drive")]);
        }).ToArray();
        var side = Ui.Column("files-side",
        [
            Ui.Text("storages-title", FilesCopy.StorageHeading),
            .. sideRows,
            Ui.Button("side-open-settings", FilesCopy.OpenSettings, Act("open-settings"), style: UiStyle.Ghost, icon: "plug"),
        ]);

        var selected = state.Selected;
        var folder = state.Breadcrumbs.LastOrDefault()?.Item.Flags ?? ItemFlags.None;
        var writable = folder.HasFlag(ItemFlags.Write);
        var create = folder.HasFlag(ItemFlags.CreateFolder);
        var one = selected is not null;
        var file = selected?.Item.Kind == ItemKind.File;
        UiNode Button(string key, string text, string verb, bool enabled, UiStyle style = UiStyle.Secondary, int? index = null, string? icon = null)
            => new(UiNodeKind.Button, key) { Text = text, Action = Act(verb, index), Enabled = enabled, Style = style, Icon = icon };
        var toolbar = Ui.Row("toolbar",
        [
            Button("upload", FilesCopy.Upload, "upload", writable, UiStyle.Primary, icon: "upload"),
            Button("mkdir", FilesCopy.NewFolder, "mkdir", create, icon: "folder-plus"),
            Button("rename", FilesCopy.Rename, "rename", one && selected!.Item.Flags.HasFlag(ItemFlags.Rename), icon: "pencil"),
            Button("move", FilesCopy.Move, "move", one && selected!.Item.Flags.HasFlag(ItemFlags.Move)),
            Button("copy", FilesCopy.Copy, "copy", one && selected!.Item.Flags.HasFlag(ItemFlags.Copy), icon: "copy"),
            state.SelectedNeuronUri is { } toolbarUri
                ? Ui.CopyButton("copy-neuron-id", FilesCopy.CopyNeuronId, toolbarUri)
                : Button("copy-neuron-id", FilesCopy.CopyNeuronId, "copy-neuron-id", false, UiStyle.Ghost),
            selected is not null
                ? Ui.CopyButton("copy-display-path", FilesCopy.CopyDisplayPath, DisplayPath(state, selected))
                : Button("copy-display-path", FilesCopy.CopyDisplayPath, "copy-display-path", false, UiStyle.Ghost),
            Button("ask-ino-toolbar", FilesCopy.AskInoAboutFile, "ask-ino", one || state.Connection is not null || state.Items.Length > 0, UiStyle.Primary, icon: "sparkles"),
            Button("download", FilesCopy.Download, "download", file && selected!.Item.Flags.HasFlag(ItemFlags.Read), UiStyle.Link, icon: "download"),
            Button("delete", FilesCopy.Delete, "delete", one && selected!.Item.Flags.HasFlag(ItemFlags.Trash), UiStyle.Danger, icon: "trash-2"),
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
        if (state.SelectedNeuronUri is { } neuronUri)
        {
            notices.Add(Ui.Text("neuron-uri", neuronUri));
            notices.Add(Ui.CopyButton("copy-selected-neuron-uri", FilesCopy.CopyNeuronId + " (" + neuronUri + ")", neuronUri));
            if (selected is not null)
            {
                notices.Add(Ui.CopyButton("copy-selected-display-path", FilesCopy.CopyDisplayPath, DisplayPath(state, selected)));
            }
        }

        var preview = Preview(state, revision, appKey);
        var itemsNode = BuildItemList(state, rev, pageOffset);
        var list = Ui.Column("files-list", [headers, .. notices, itemsNode]);
        var chrome = new UiNode[]
        {
            Ui.Row("navigation",
            [
                Button("up", FilesCopy.Up, "up", state.Connection is not null, icon: "arrow-up"),
                Button("search-all", "Search All Storages", "search-all", true, icon: "search"),
                Button("apply-filter", "Filter", "filter", true, UiStyle.Ghost, icon: "filter"),
            ]),
            Crumbs(state, revision),
            toolbar,
            Ui.Reference("filter", new(IntoChat.OS.Modules.Flutter.UIVocabulary.TextFieldType, appKey + "/filter")),
            Ui.Reference("search", new(IntoChat.OS.Modules.Flutter.UIVocabulary.TextFieldType, appKey + "/search")),
        };
        var main = narrow && previewOpen && preview is not null
            ? Ui.Column("files-main", [Ui.Button("back-files", FilesCopy.BackToFiles, Act("back-files")), preview])
            : Ui.Column("files-main", [.. chrome, preview is null ? list : Ui.Row("browse-split", [list, preview], extents: [1, 360])]);
        return new(side, main, files, state.PreviewUrl);
    }

    private static UiNode BuildItemList(FilesState state, string rev, int pageOffset)
    {
        var rows = new List<UiNode>();
        if (state.Searching)
        {
            var itemIdx = 0;
            foreach (var group in state.Groups)
            {
                rows.Add(Ui.Divider("group-div-" + group.Connection, group.Label));
                foreach (var entry in group.Items.Take(40))
                {
                    if (itemIdx >= 40) { break; }
                    var rawIndex = Array.IndexOf(state.Items, entry);
                    if (rawIndex < 0) { continue; }
                    var isFolder = entry.Item.Kind == ItemKind.Folder;
                    var isSelected = state.Selected is { } sel && sel.Connection == entry.Connection && sel.Item.Id == entry.Item.Id;
                    rows.Add(Ui.Row("search-row-" + itemIdx,
                    [
                        Ui.Button(
                            "search-item-" + itemIdx,
                            entry.Item.Name,
                            rev + ":" + (isFolder ? "browse" : "select") + ":" + rawIndex.ToString(CultureInfo.InvariantCulture),
                            style: isSelected ? UiStyle.Primary : UiStyle.Ghost,
                            icon: isFolder ? "folder" : "file"),
                        Ui.Badge("search-meta-" + itemIdx, FormatItemMeta(state, entry), isFolder ? UiTone.Neutral : UiTone.Working),
                    ]));
                    itemIdx++;
                }
            }
            return Ui.List("items", [.. rows]);
        }

        var ordered = Ordered(state);
        var start = Math.Clamp(pageOffset, 0, Math.Max(0, ordered.Count - 1));
        var slice = ordered.Skip(start).Take(40).ToArray();
        for (var i = 0; i < slice.Length; i++)
        {
            var entry = slice[i];
            var rawIndex = Array.IndexOf(state.Items, entry);
            if (rawIndex < 0) { continue; }
            var isFolder = entry.Item.Kind == ItemKind.Folder;
            var isSelected = state.Selected is { } sel && sel.Connection == entry.Connection && sel.Item.Id == entry.Item.Id;
            rows.Add(Ui.Row("item-row-" + i,
            [
                Ui.Button(
                    "item-open-" + i,
                    entry.Item.Name,
                    rev + ":" + (isFolder ? "browse" : "select") + ":" + rawIndex.ToString(CultureInfo.InvariantCulture),
                    style: isSelected ? UiStyle.Primary : UiStyle.Ghost,
                    icon: isFolder ? "folder" : "file"),
                Ui.Badge("item-meta-" + i, FormatItemMeta(state, entry), isFolder ? UiTone.Neutral : UiTone.Working),
                Ui.Button(
                    "item-select-" + i,
                    isFolder ? "Select" : (isSelected ? "Selected" : "Inspect"),
                    rev + ":select:" + rawIndex.ToString(CultureInfo.InvariantCulture),
                    style: isSelected ? UiStyle.Primary : UiStyle.Secondary),
            ], gap: 8, extents: [0, 140, 96]));
        }

        if (ordered.Count > 40)
        {
            rows.Add(Ui.Row("items-pager",
            [
                Ui.Button("page-prev", "Previous 40", rev + ":previous", enabled: start > 0, style: UiStyle.Secondary),
                Ui.Badge("page-info", $"{start + 1}-{Math.Min(ordered.Count, start + slice.Length)} of {ordered.Count}", UiTone.Neutral),
                Ui.Button("page-next", "Next 40", rev + ":next", enabled: start + 40 < ordered.Count, style: UiStyle.Secondary),
            ]));
        }

        return Ui.List("items", [.. rows]);
    }

    private static string FormatItemMeta(FilesState state, FilesItem entry)
    {
        var item = entry.Item;
        var modPart = item.Modified is { } mod
            ? mod.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "—";
        var whereSuffix = state.Connection is null && !state.Searching
            ? " · " + (state.Storages.FirstOrDefault(s => string.Equals(s.Connection, entry.Connection, StringComparison.Ordinal))?.Label ?? entry.Connection)
            : "";
        if (item.Kind == ItemKind.Folder)
        {
            return $"Folder · -- · {modPart}{whereSuffix}";
        }
        var sizePart = item.Size is { } bytes
            ? bytes < 1024
                ? $"{bytes} B"
                : bytes < 1048576
                    ? $"{bytes / 1024.0:F1} KB"
                    : bytes < 1073741824
                        ? $"{bytes / 1048576.0:F1} MB"
                        : $"{bytes / 1073741824.0:F2} GB"
            : "--";
        return $"File · {sizePart} · {modPart}{whereSuffix}";
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

    private static string DisplayPath(FilesState state, FilesItem selected)
    {
        var crumbs = state.Breadcrumbs
            .Where(c => c.Connection == selected.Connection)
            .Select(c => c.Item.Name)
            .Append(selected.Item.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name));
        var joined = string.Join("/", crumbs);
        return string.IsNullOrWhiteSpace(joined) ? selected.Item.Name : joined;
    }

    private static string? ActiveFolderDisplayPath(FilesState state)
    {
        if (state.Breadcrumbs.Length > 0)
        {
            return string.Join("/", state.Breadcrumbs.Select(c => c.Item.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
        }
        if (state.Connection is { } conn)
        {
            return state.Storages.FirstOrDefault(s => s.Connection == conn)?.Label ?? conn;
        }
        return null;
    }

    private static string? ActiveFolderNeuronUri(FilesState state)
    {
        if (state.SelectedNeuronUri is { } selectedUri)
        {
            return selectedUri;
        }
        var connection = state.Connection ?? state.Breadcrumbs.LastOrDefault()?.Connection;
        if (string.IsNullOrWhiteSpace(connection))
        {
            return null;
        }
        var segments = state.Breadcrumbs
            .Skip(1)
            .Select(folder => SlugSegment(folder.Item.Name))
            .Where(s => s.Length > 0)
            .ToArray();
        var suffix = segments.Length == 0 ? "" : "/" + string.Join("/", segments);
        return $"neuron://owner/{SlugSegment(connection)}.files{suffix}";
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

    private static UiNode Crumbs(FilesState state, long revision)
    {
        var rev = (revision != 0 ? revision : state.Revision).ToString(CultureInfo.InvariantCulture);
        var nodes = new List<UiNode>
        {
            Ui.Button(
                "crumb-root",
                FilesCopy.StorageHeading,
                rev + ":open",
                style: state.Connection is null ? UiStyle.Primary : UiStyle.Ghost,
                icon: "hard-drive"),
        };

        if (state.Connection is { } activeConn)
        {
            var storageIdx = Array.FindIndex(state.Storages, s => string.Equals(s.Connection, activeConn, StringComparison.Ordinal));
            var storageLabel = storageIdx >= 0 ? state.Storages[storageIdx].Label : activeConn;
            var storageAction = storageIdx >= 0
                ? rev + ":storage:" + storageIdx.ToString(CultureInfo.InvariantCulture)
                : rev + ":open";
            nodes.Add(Ui.Button(
                "crumb-storage",
                storageLabel,
                storageAction,
                style: state.Breadcrumbs.Length == 0 ? UiStyle.Primary : UiStyle.Ghost,
                icon: "folder"));
        }

        for (var index = 0; index < state.Breadcrumbs.Length; index++)
        {
            var item = state.Breadcrumbs[index];
            nodes.Add(Ui.Button(
                "crumb-" + index,
                item.Item.Name,
                rev + ":breadcrumb:" + index.ToString(CultureInfo.InvariantCulture),
                style: index == state.Breadcrumbs.Length - 1 ? UiStyle.Primary : UiStyle.Ghost,
                icon: "folder"));
        }

        var activeUri = ActiveFolderNeuronUri(state);
        var activeDisplayPath = state.Selected is { } sel ? DisplayPath(state, sel) : ActiveFolderDisplayPath(state);

        if (activeUri is { Length: > 0 })
        {
            nodes.Add(Ui.Badge("crumb-neuron-uri", activeUri, UiTone.Neutral));
            nodes.Add(Ui.CopyButton("crumb-copy-neuron-uri", FilesCopy.CopyNeuronUri + " (" + FilesCopy.CopyNeuronUriShortcut + ")", activeUri));
        }
        else
        {
            nodes.Add(Ui.Button("crumb-copy-neuron-uri", FilesCopy.CopyNeuronUri + " (" + FilesCopy.CopyNeuronUriShortcut + ")", rev + ":copy-neuron-uri", enabled: false, style: UiStyle.Ghost));
        }

        if (activeDisplayPath is { Length: > 0 })
        {
            nodes.Add(Ui.CopyButton("crumb-copy-display-path", FilesCopy.CopyDisplayPath, activeDisplayPath));
        }
        else
        {
            nodes.Add(Ui.Button("crumb-copy-display-path", FilesCopy.CopyDisplayPath, rev + ":copy-display-path", enabled: false, style: UiStyle.Ghost));
        }

        return Ui.Row("breadcrumbs", [.. nodes]);
    }

    private static UiNode? Preview(FilesState state, long revision, string appKey)
    {
        var selected = state.Selected;
        if (selected is null && state.PreviewApp is null && state.PreviewUrl is null && state.Preview is null)
        {
            return null;
        }

        var rev = (revision != 0 ? revision : state.Revision).ToString(CultureInfo.InvariantCulture);
        var children = new List<UiNode>
        {
            Ui.Badge("quick-view-badge", FilesCopy.QuickViewHeading, UiTone.Neutral),
        };

        if (selected is not null)
        {
            var kindText = selected.Item.Kind == ItemKind.Folder
                ? "Folder"
                : (Path.GetExtension(selected.Item.Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext + " file" : "File");
            var sizeText = selected.Item.Size is { } bytes ? $" · {bytes} B" : "";
            children.Add(Ui.Text("quick-view-meta", $"{kindText}{sizeText} · {DisplayPath(state, selected)}"));
        }

        if (state.SelectedNeuronUri is { } qvUri)
        {
            children.Add(Ui.Text("quick-view-uri", qvUri));
        }

        children.Add(Ui.Row("quick-view-actions",
        [
            Ui.Button("ask-ino", FilesCopy.AskInoAboutFile, rev + ":ask-ino", enabled: selected is not null, style: UiStyle.Primary, icon: "sparkles"),
            state.SelectedNeuronUri is { } copyUri
                ? Ui.CopyButton("quick-view-copy-neuron-uri", FilesCopy.CopyNeuronUri, copyUri)
                : Ui.Button("quick-view-copy-neuron-uri", FilesCopy.CopyNeuronUri, rev + ":copy-neuron-uri", enabled: false, style: UiStyle.Ghost),
            selected is not null
                ? Ui.CopyButton("quick-view-copy-display-path", FilesCopy.CopyDisplayPath, DisplayPath(state, selected))
                : Ui.Button("quick-view-copy-display-path", FilesCopy.CopyDisplayPath, rev + ":copy-display-path", enabled: false, style: UiStyle.Ghost),
        ]));

        if (state.InoHandoffPrompt is { } handoff)
        {
            children.Add(Ui.Text("ino-handoff-prompt", handoff));
        }

        if (state.PreviewApp is { } app)
        {
            children.Add(Ui.Card("preview-app", app, null, [Ui.Button("open-app", FilesCopy.OpenApp, rev + ":open-app")]));
        }
        else if (state.PreviewUrl is { } url)
        {
            children.Add(Ui.Column("preview", [Ui.Text("preview-url", url), Ui.Reference("preview-image", new(IntoChat.OS.Modules.Flutter.UIVocabulary.ImageType, appKey + "/preview"))]));
        }
        else if (state.Preview is { } text)
        {
            var previewNodes = new List<UiNode> { Ui.Text("preview-text", text) };
            if (state.PreviewNote is { } note) { previewNodes.Add(Ui.Text("preview-note", note)); }
            children.Add(Ui.Column("preview", [.. previewNodes]));
        }

        return Ui.Card(
            "quick-view",
            selected?.Item.Name ?? FilesCopy.QuickViewHeading,
            state.SelectedNeuronUri ?? FilesCopy.QuickViewHeading,
            [.. children]);
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
