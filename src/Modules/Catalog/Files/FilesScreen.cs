using System.Globalization;
using System.Text;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.Files;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Modules.Apps;

// Finder-style Files screen built on IntoChat.OS.UI.Kit (`UK-17` / `INT-1365`):
// HeaderBar, clickable path breadcrumb bar, SearchField, list/grid view-mode toggle,
// ResponsiveSplit (Table/Grid file listing + right-hand QuickView inspector), and crisp Empty states.
internal static class FilesScreen
{
    internal sealed record Model(Node Side, Node Main, CollectionDefinition Files, string? ImageUrl);

    public static Model Compose(
        FilesState state,
        int width,
        bool previewOpen,
        long revision = 0,
        string appKey = "owner/apps/files",
        int pageOffset = 0,
        string viewMode = "list")
    {
        ArgumentNullException.ThrowIfNull(state);
        var narrow = width <= 390;
        var isGridView = string.Equals(viewMode, "grid", StringComparison.OrdinalIgnoreCase);
        var rev = (revision != 0 ? revision : state.Revision).ToString(CultureInfo.InvariantCulture);
        Emit Act(string verb, int? index = null) => FilesUi.Act(rev, verb, index);

        if (state.Storages.Length == 0 && state.Connection is null && !state.Loading && !state.Searching)
        {
            var emptyMain = VStack(Space.Md)[
                HeaderBar("Files", "Connected storages & hybrid local mounts"),
                Empty(FilesCopy.NoStorageHeading, FilesCopy.NoStorageBody),
                HStack(Space.Sm)[
                    FilesUi.Button("open-settings", FilesCopy.OpenSettings, Act("open-settings"), style: FilesUi.Primary, icon: "plug")
                ]
            ].ToNode();
            var emptySide = VStack(Space.Sm)[
                Heading(FilesCopy.StorageHeading, 3),
                MutedText("No connected locations")
            ].ToNode();
            return new(emptySide, emptyMain, new([], Error: null), null);
        }

        var sideRows = state.Storages.Select((storage, index) =>
        {
            var quota = storage.Error is null ? QuotaText.For(storage.Space ?? Quota.Unknown, storage.Kind) : FilesCopy.Unavailable;
            var isCurrentStorage = string.Equals(state.Connection, storage.Connection, StringComparison.Ordinal);
            return Card(storage.Label, quota)[
                HStack(Space.Xs)[
                    StatusPill(
                        storage.Error is not null ? StatusKind.Error : (isCurrentStorage ? StatusKind.Done : StatusKind.Idle),
                        storage.Error is not null ? FilesCopy.Unavailable : (isCurrentStorage ? "Active" : storage.Kind)),
                    FilesUi.Button(
                        "storage-open-" + index,
                        storage.Error is null ? "Browse" : FilesCopy.TryAgain,
                        Act(storage.Error is null ? "storage" : "retry", index),
                        style: isCurrentStorage ? FilesUi.Primary : FilesUi.Secondary,
                        icon: storage.Kind == "google-drive" ? "cloud" : "hard-drive")
                ]
            ].ToNode();
        }).ToArray();

        var side = VStack(
            Space.Sm,
            [
                Heading(FilesCopy.StorageHeading, 3, $"{state.Storages.Length} location(s)"),
                .. sideRows,
                Divider(),
                FilesUi.Button("side-open-settings", FilesCopy.OpenSettings, Act("open-settings"), style: FilesUi.Ghost, icon: "plug"),
            ]).ToNode();

        var selected = state.Selected;
        var folder = state.Breadcrumbs.LastOrDefault()?.Item.Flags ?? ItemFlags.None;
        var writable = folder.HasFlag(ItemFlags.Write);
        var create = folder.HasFlag(ItemFlags.CreateFolder);
        var one = selected is not null;
        var file = selected?.Item.Kind == ItemKind.File;

        Node Button(
            string key,
            string text,
            string verb,
            bool enabled,
            string style = FilesUi.Secondary,
            int? index = null,
            string? icon = null,
            Rule? guard = null)
            => FilesUi.Button(key, text, Act(verb, index), enabled, style, icon, guard);

        Node CopyOrDisabled(string key, string label, string? value, string disabledVerb)
            => value is { Length: > 0 } ? FilesUi.CopyButton(key, label, value) : Button(key, label, disabledVerb, false, FilesUi.Ghost);

        var headerTitle = ActiveFolderDisplayPath(state) ?? "Files";
        var headerSubtitle = state.Searching
            ? $"Search results for \"{state.Search}\""
            : $"{state.Items.Length} item(s)";

        var finderHeader = HeaderBar(
            headerTitle,
            headerSubtitle,
            Button("up", FilesCopy.Up, "up", state.Connection is not null, icon: "arrow-up"),
            Button("view-list", "List", "view-list", true, !isGridView ? FilesUi.Primary : FilesUi.Ghost, icon: "list"),
            Button("view-grid", "Grid", "view-grid", true, isGridView ? FilesUi.Primary : FilesUi.Ghost, icon: "layout-grid"),
            Button("search-all", "Search All Storages", "search-all", true, icon: Icons.Search),
            Button("apply-filter", "Filter", "filter", true, FilesUi.Ghost, icon: "filter"));

        var toolbar = HStack(Space.Xs)[
            Button("upload", FilesCopy.Upload, "upload", writable, FilesUi.Primary, icon: "upload", guard: FilesPathRules.CanWriteFolder(writable)),
            Button("mkdir", FilesCopy.NewFolder, "mkdir", create, icon: "folder-plus", guard: FilesPathRules.CanCreateSubfolder(create)),
            Button("rename", FilesCopy.Rename, "rename", one && selected!.Item.Flags.HasFlag(ItemFlags.Rename), icon: "pencil"),
            Button("move", FilesCopy.Move, "move", one && selected!.Item.Flags.HasFlag(ItemFlags.Move)),
            Button("copy", FilesCopy.Copy, "copy", one && selected!.Item.Flags.HasFlag(ItemFlags.Copy), icon: "copy"),
            CopyOrDisabled("copy-neuron-id", FilesCopy.CopyNeuronId, state.SelectedNeuronUri, "copy-neuron-id"),
            CopyOrDisabled("copy-display-path", FilesCopy.CopyDisplayPath, selected is not null ? DisplayPath(state, selected) : null, "copy-display-path"),
            Button("ask-ino-toolbar", FilesCopy.AskInoAboutFile, "ask-ino", one || state.Connection is not null || state.Items.Length > 0, FilesUi.Primary, icon: Icons.Sparkles),
            Button("download", FilesCopy.Download, "download", file && selected!.Item.Flags.HasFlag(ItemFlags.Read), FilesUi.Link, icon: "download"),
            Button("delete", FilesCopy.Delete, "delete", one && selected!.Item.Flags.HasFlag(ItemFlags.Trash), FilesUi.Danger, icon: Icons.Trash2)
        ].ToNode();

        var files = state.Searching ? SearchCollection(state) : FolderCollection(state);
        var headers = HStack(Space.Sm)[
            Button("sort-name", FilesCopy.NameHeader, "sort-name", true, state.Sort == "name" ? FilesUi.Primary : FilesUi.Ghost),
            Button("sort-size", FilesCopy.SizeHeader, "sort-size", true, state.Sort == "size" ? FilesUi.Primary : FilesUi.Ghost),
            Button("sort-modified", FilesCopy.ModifiedHeader, "sort-modified", true, state.Sort == "modified" ? FilesUi.Primary : FilesUi.Ghost),
            StatusPill(StatusKind.Idle, isGridView ? "Grid view" : "List view")
        ].ToNode();

        var notices = new List<Node>();
        if (state.Loading)
        {
            notices.Add(HStack(Space.Sm)[
                StatusPill(StatusKind.Working, FilesCopy.Loading),
                Text(FilesCopy.Loading)
            ]);
        }

        if (state.StatusTitle is { } title)
        {
            notices.Add(Banner(
                title,
                state.StatusBody ?? FilesCopy.NothingChanged,
                tone: state.StatusSeverity == "warning" ? "warning" : "error"));
            notices.Add(Text(title));
            if (state.StatusBody is { } body)
            {
                notices.Add(MutedText(body));
            }

            if (state.StatusSeverity == "warning")
            {
                notices.Add(HStack(Space.Sm)[
                    FilesUi.Button("try-again", FilesCopy.TryAgain, Act("retry"), style: FilesUi.Primary, icon: Icons.RefreshCw)
                ]);
            }
        }

        if (!state.Searching && state.Filter.Length > 0 && files.Items.Length == 0 && !state.Loading && state.StatusTitle is null)
        {
            notices.Add(Empty(FilesCopy.NoMatchHeading, FilesCopy.NoMatchBody(state.Filter)));
            notices.Add(HStack(Space.Sm)[
                FilesUi.Button("clear-filter", FilesCopy.ClearFilter, Act("clear-filter"), style: FilesUi.Primary, icon: "x")
            ]);
        }

        if (!state.Searching && state.Items.Length == 0 && state.Filter.Length == 0 && !state.Loading && state.StatusTitle is null && state.Connection is not null)
        {
            notices.Add(Empty(FilesCopy.EmptyHeading, FilesCopy.EmptyBody));
        }

        foreach (var group in state.Searching ? state.Groups.Where(group => group.Bounded && !group.Failed) : [])
        {
            notices.Add(MutedText(FilesCopy.Bounded(group.Items.Length)));
        }

        if (!state.Searching && state.PreviousConnection is not null)
        {
            notices.Add(FilesUi.Button("back-folder", FilesCopy.BackToFolder, Act("back-folder"), icon: "arrow-left"));
        }

        if (state.Pages.Any(page => !page.Complete))
        {
            notices.Add(FilesUi.Button("more", FilesCopy.LoadMore, Act("more"), icon: "chevrons-down"));
        }

        if (state.DownloadUrl is { } download)
        {
            notices.Add(MutedText(download).Tabular());
        }

        if (state.SelectedNeuronUri is { } neuronUri)
        {
            notices.Add(HStack(Space.Sm)[
                Text(neuronUri).Tabular(),
                FilesUi.CopyButton("copy-selected-neuron-uri", FilesCopy.CopyNeuronId + " (" + neuronUri + ")", neuronUri),
                selected is not null
                    ? FilesUi.CopyButton("copy-selected-display-path", FilesCopy.CopyDisplayPath, DisplayPath(state, selected))
                    : null
            ]);
        }

        var preview = Preview(state, revision);
        var itemsNode = BuildItemList(state, rev, pageOffset, isGridView);
        var list = Surface(SurfaceKind.Elevated, "Directory Listing", isGridView ? "Finder tile grid" : "Sortable file table")[
            VStack(Space.Sm, [headers, .. notices, itemsNode])
        ].ToNode();

        var searchBarRow = Grid(2, Space.Sm)[
            FilesUi.Field("filter", appKey + "/filter", FieldKind.Search, FilesCopy.FilterLabel),
            FilesUi.Field("search", appKey + "/search", FieldKind.Search, FilesCopy.SearchLabel)
        ].ToNode();

        var chrome = new Node[]
        {
            finderHeader,
            Crumbs(state, revision),
            toolbar,
            searchBarRow,
        };

        var main = narrow && previewOpen && preview is not null
            ? VStack(Space.Md)[
                FilesUi.Button("back-files", FilesCopy.BackToFiles, Act("back-files"), icon: "arrow-left"),
                preview
            ].ToNode()
            : VStack(
                Space.Md,
                [
                    .. chrome,
                    preview is null
                        ? list
                        : ResponsiveSplit(leading: list, trailing: preview, leadingExtent: 560, gap: Space.Md),
                ]).ToNode();

        return new(
            side,
            main,
            files,
            state.PreviewUrl is { } previewUrl && FilesApp.IsImage(state.Selected?.Item.Name ?? "") ? previewUrl : null);
    }

    private static Node BuildItemList(FilesState state, string rev, int pageOffset, bool isGridView)
    {
        var rows = new List<Node>();
        if (state.Searching)
        {
            var itemIdx = 0;
            foreach (var group in state.Groups)
            {
                rows.Add(Divider(group.Label));
                foreach (var entry in group.Items.Take(40))
                {
                    if (itemIdx >= 40) { break; }
                    var rawIndex = Array.IndexOf(state.Items, entry);
                    if (rawIndex < 0) { continue; }
                    var isFolder = entry.Item.Kind == ItemKind.Folder;
                    var isSelected = state.Selected is { } sel && sel.Connection == entry.Connection && sel.Item.Id == entry.Item.Id;
                    rows.Add(HStack(Space.Sm)[
                        FilesUi.Button(
                            "search-item-" + itemIdx,
                            entry.Item.Name,
                            FilesUi.Act(rev, isFolder ? "browse" : "select", rawIndex),
                            style: isSelected ? FilesUi.Primary : FilesUi.Ghost,
                            icon: isFolder ? Icons.Folder : "file"),
                        FilesUi.Badge(FormatItemMeta(state, entry), isFolder ? FilesUi.Neutral : FilesUi.Working)
                    ]);
                    itemIdx++;
                }
            }

            return FilesUi.List([.. rows]);
        }

        var ordered = Ordered(state);
        if (ordered.Count == 0)
        {
            return FilesUi.List([]);
        }

        var start = Math.Clamp(pageOffset, 0, Math.Max(0, ordered.Count - 1));
        var slice = ordered.Skip(start).Take(40).ToArray();

        if (isGridView)
        {
            var tiles = new List<Node>(slice.Length);
            for (var i = 0; i < slice.Length; i++)
            {
                var entry = slice[i];
                var rawIndex = Array.IndexOf(state.Items, entry);
                if (rawIndex < 0) { continue; }
                var isFolder = entry.Item.Kind == ItemKind.Folder;
                var isSelected = state.Selected is { } sel && sel.Connection == entry.Connection && sel.Item.Id == entry.Item.Id;
                tiles.Add(Card(entry.Item.Name, FormatItemSize(entry.Item))[
                    VStack(Space.Xs)[
                        StatusPill(isFolder ? StatusKind.Idle : StatusKind.Done, isFolder ? "Folder" : FileExtensionBadge(entry.Item.Name)),
                        HStack(Space.Xs)[
                            FilesUi.Button(
                                "item-open-" + i,
                                entry.Item.Name,
                                FilesUi.Act(rev, isFolder ? "browse" : "select", rawIndex),
                                style: isSelected ? FilesUi.Primary : FilesUi.Ghost,
                                icon: isFolder ? Icons.Folder : "file"),
                            FilesUi.Button(
                                "item-select-" + i,
                                isFolder ? "Select" : (isSelected ? "Selected" : "Inspect"),
                                FilesUi.Act(rev, "select", rawIndex),
                                style: isSelected ? FilesUi.Primary : FilesUi.Secondary)
                        ]
                    ]
                ]);
            }

            rows.Add(Grid(3, Space.Sm, tiles.ToArray()));
        }
        else
        {
            var tableSummaryRows = slice
                .Select(entry => (IReadOnlyList<string>)
                [
                    entry.Item.Name,
                    entry.Item.Kind == ItemKind.Folder ? "Folder" : FileExtensionBadge(entry.Item.Name),
                    FormatItemSize(entry.Item),
                    FormatItemModified(entry.Item),
                ])
                .ToArray();

            rows.Add(Table(
                "Folder contents",
                [FilesCopy.NameHeader, "Kind", FilesCopy.SizeHeader, FilesCopy.ModifiedHeader],
                tableSummaryRows));

            for (var i = 0; i < slice.Length; i++)
            {
                var entry = slice[i];
                var rawIndex = Array.IndexOf(state.Items, entry);
                if (rawIndex < 0) { continue; }
                var isFolder = entry.Item.Kind == ItemKind.Folder;
                var isSelected = state.Selected is { } sel && sel.Connection == entry.Connection && sel.Item.Id == entry.Item.Id;
                rows.Add(FilesUi.Row(
                [
                    FilesUi.Button(
                        "item-open-" + i,
                        entry.Item.Name,
                        FilesUi.Act(rev, isFolder ? "browse" : "select", rawIndex),
                        style: isSelected ? FilesUi.Primary : FilesUi.Ghost,
                        icon: isFolder ? Icons.Folder : "file"),
                    FilesUi.Badge(FormatItemMeta(state, entry), isFolder ? FilesUi.Neutral : FilesUi.Working),
                    FilesUi.Button(
                        "item-select-" + i,
                        isFolder ? "Select" : (isSelected ? "Selected" : "Inspect"),
                        FilesUi.Act(rev, "select", rawIndex),
                        style: isSelected ? FilesUi.Primary : FilesUi.Secondary),
                ], gap: Space.Sm, extents: [0, 180, 96]));
            }
        }

        if (ordered.Count > 40)
        {
            rows.Add(HStack(Space.Sm)[
                FilesUi.Button("page-prev", "Previous 40", FilesUi.Act(rev, "previous"), enabled: start > 0, style: FilesUi.Secondary),
                FilesUi.Badge($"{start + 1}-{Math.Min(ordered.Count, start + slice.Length)} of {ordered.Count}", FilesUi.Neutral),
                FilesUi.Button("page-next", "Next 40", FilesUi.Act(rev, "next"), enabled: start + 40 < ordered.Count, style: FilesUi.Secondary)
            ]);
        }

        return FilesUi.List([.. rows]);
    }

    private static string FileExtensionBadge(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        return ext.Length > 0 ? ext : "FILE";
    }

    private static string FormatItemSize(Item item)
    {
        if (item.Kind == ItemKind.Folder) { return "--"; }
        return item.Size is { } bytes
            ? bytes < 1024 ? $"{bytes} B"
            : bytes < 1048576 ? $"{bytes / 1024.0:F1} KB"
            : bytes < 1073741824 ? $"{bytes / 1048576.0:F1} MB"
            : $"{bytes / 1073741824.0:F2} GB"
            : "--";
    }

    private static string FormatItemModified(Item item)
        => item.Modified is { } mod
            ? mod.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "—";

    private static string FormatItemMeta(FilesState state, FilesItem entry)
    {
        var item = entry.Item;
        var modPart = FormatItemModified(item);
        var whereSuffix = state.Connection is null && !state.Searching
            ? " · " + (state.Storages.FirstOrDefault(s => string.Equals(s.Connection, entry.Connection, StringComparison.Ordinal))?.Label ?? entry.Connection)
            : "";
        if (item.Kind == ItemKind.Folder) { return $"Folder · -- · {modPart}{whereSuffix}"; }
        return $"File · {FormatItemSize(item)} · {modPart}{whereSuffix}";
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
        return new(
            rows,
            Selection: selection,
            Error: null,
            FileMode: true,
            EmptyLabel: rows.Length == 0 ? empty : null,
            Breadcrumbs: [.. state.Breadcrumbs.Select(item => new CollectionBreadcrumb(item.Item.Id.Value, item.Item.Name))]);
    }

    private static CollectionDefinition SearchCollection(FilesState state)
    {
        var items = new List<CollectionItem>();
        foreach (var group in state.Groups)
        {
            items.Add(new("group:" + group.Connection, group.Label, "group", CanActivate: false));
            if (group.Failed) { items.Add(new("notice:" + group.Connection, FilesCopy.GroupSilent(group.Label), "group", CanActivate: false)); }
            else if (group.Items.Length == 0) { items.Add(new("empty:" + group.Connection, FilesCopy.NoMatchesIn(group.Label), "group", CanActivate: false)); }
            else { items.AddRange(group.Items.Select(Row)); }
        }

        return new([.. items], Error: null, FileMode: true, EmptyLabel: null);
    }

    private static CollectionItem Row(FilesItem entry)
    {
        var folder = entry.Item.Kind == ItemKind.Folder;
        var kind = folder ? "folder" : Path.GetExtension(entry.Item.Name).TrimStart('.').ToLowerInvariant();
        if (kind.Length == 0) { kind = "file"; }
        return new(
            RowId(entry),
            entry.Item.Name,
            kind,
            SizeBytes: folder ? null : entry.Item.Size,
            ModifiedAt: entry.Item.Modified,
            TrailingText: folder ? "" : null);
    }

    private static string RowId(FilesItem entry) => entry.Connection + "/" + entry.Item.Id.Value;

    private static string DisplayPath(FilesState state, FilesItem selected)
    {
        var joined = string.Join("/", state.Breadcrumbs.Where(c => c.Connection == selected.Connection).Select(c => c.Item.Name).Append(selected.Item.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
        return string.IsNullOrWhiteSpace(joined) ? selected.Item.Name : joined;
    }

    private static string? ActiveFolderDisplayPath(FilesState state) => state.Breadcrumbs.Length > 0
        ? string.Join("/", state.Breadcrumbs.Select(c => c.Item.Name).Where(n => !string.IsNullOrWhiteSpace(n)))
        : state.Connection is { } conn ? state.Storages.FirstOrDefault(s => s.Connection == conn)?.Label ?? conn : null;

    private static string? ActiveFolderNeuronUri(FilesState state)
    {
        if (state.SelectedNeuronUri is { } selectedUri) { return selectedUri; }
        var connection = state.Connection ?? state.Breadcrumbs.LastOrDefault()?.Connection;
        if (string.IsNullOrWhiteSpace(connection)) { return null; }
        var segments = state.Breadcrumbs.Skip(1).Select(folder => SlugSegment(folder.Item.Name)).Where(s => s.Length > 0).ToArray();
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

    private static Node Crumbs(FilesState state, long revision)
    {
        var rev = (revision != 0 ? revision : state.Revision).ToString(CultureInfo.InvariantCulture);
        var nodes = new List<Node>
        {
            FilesUi.Button(
                "crumb-root",
                FilesCopy.StorageHeading,
                FilesUi.Act(rev, "open"),
                style: state.Connection is null ? FilesUi.Primary : FilesUi.Ghost,
                icon: "hard-drive"),
        };

        if (state.Connection is { } activeConn)
        {
            var storageIdx = Array.FindIndex(state.Storages, s => string.Equals(s.Connection, activeConn, StringComparison.Ordinal));
            var storageLabel = storageIdx >= 0 ? state.Storages[storageIdx].Label : activeConn;
            var storageAction = storageIdx >= 0 ? FilesUi.Act(rev, "storage", storageIdx) : FilesUi.Act(rev, "open");
            nodes.Add(MutedText("/"));
            nodes.Add(FilesUi.Button(
                "crumb-storage",
                storageLabel,
                storageAction,
                style: state.Breadcrumbs.Length == 0 ? FilesUi.Primary : FilesUi.Ghost,
                icon: Icons.Folder));
        }

        for (var index = 0; index < state.Breadcrumbs.Length; index++)
        {
            var item = state.Breadcrumbs[index];
            nodes.Add(MutedText("/"));
            nodes.Add(FilesUi.Button(
                "crumb-" + index,
                item.Item.Name,
                FilesUi.Act(rev, "breadcrumb", index),
                style: index == state.Breadcrumbs.Length - 1 ? FilesUi.Primary : FilesUi.Ghost,
                icon: Icons.Folder));
        }

        nodes.Add(Spacer(Space.Xs));

        var activeUri = ActiveFolderNeuronUri(state);
        var activeDisplayPath = state.Selected is { } sel ? DisplayPath(state, sel) : ActiveFolderDisplayPath(state);

        if (activeUri is { Length: > 0 })
        {
            nodes.Add(StatusPill(StatusKind.Idle, activeUri));
            nodes.Add(FilesUi.CopyButton("crumb-copy-neuron-uri", FilesCopy.CopyNeuronUri + " (" + FilesCopy.CopyNeuronUriShortcut + ")", activeUri));
        }
        else
        {
            nodes.Add(FilesUi.Button("crumb-copy-neuron-uri", FilesCopy.CopyNeuronUri + " (" + FilesCopy.CopyNeuronUriShortcut + ")", FilesUi.Act(rev, "copy-neuron-uri"), enabled: false, style: FilesUi.Ghost));
        }

        nodes.Add(activeDisplayPath is { Length: > 0 }
            ? FilesUi.CopyButton("crumb-copy-display-path", FilesCopy.CopyDisplayPath, activeDisplayPath)
            : FilesUi.Button("crumb-copy-display-path", FilesCopy.CopyDisplayPath, FilesUi.Act(rev, "copy-display-path"), enabled: false, style: FilesUi.Ghost));

        return Surface(SurfaceKind.Recessed, padding: Space.Sm)[
            HStack(Space.Xs, nodes.ToArray())
        ];
    }

    private static Node? Preview(FilesState state, long revision)
    {
        var selected = state.Selected;
        if (selected is null && state.PreviewApp is null && state.PreviewUrl is null && state.Preview is null)
        {
            return null;
        }

        var rev = (revision != 0 ? revision : state.Revision).ToString(CultureInfo.InvariantCulture);
        var children = new List<Node>
        {
            StatusPill(StatusKind.Done, FilesCopy.QuickViewHeading),
        };

        if (selected is not null)
        {
            var kindText = selected.Item.Kind == ItemKind.Folder
                ? "Folder"
                : (Path.GetExtension(selected.Item.Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext + " file" : "File");
            var sizeText = selected.Item.Size is { } bytes ? $" · {bytes} B" : "";
            children.Add(Text($"{kindText}{sizeText} · {DisplayPath(state, selected)}").Tabular());
            children.Add(Table(
                "Metadata",
                ["Field", "Value"],
                [
                    ["Name", selected.Item.Name],
                    ["Type", kindText],
                    ["Size", FormatItemSize(selected.Item)],
                    ["Modified", FormatItemModified(selected.Item)],
                    ["Storage", selected.Connection],
                ]));
        }

        if (state.SelectedNeuronUri is { } qvUri)
        {
            children.Add(MutedText(qvUri).Tabular());
        }

        children.Add(HStack(Space.Xs)[
            FilesUi.Button("ask-ino", FilesCopy.AskInoAboutFile, FilesUi.Act(rev, "ask-ino"), enabled: selected is not null, style: FilesUi.Primary, icon: Icons.Sparkles),
            state.SelectedNeuronUri is { } copyUri
                ? FilesUi.CopyButton("quick-view-copy-neuron-uri", FilesCopy.CopyNeuronUri, copyUri)
                : FilesUi.Button("quick-view-copy-neuron-uri", FilesCopy.CopyNeuronUri, FilesUi.Act(rev, "copy-neuron-uri"), enabled: false, style: FilesUi.Ghost),
            selected is not null
                ? FilesUi.CopyButton("quick-view-copy-display-path", FilesCopy.CopyDisplayPath, DisplayPath(state, selected))
                : FilesUi.Button("quick-view-copy-display-path", FilesCopy.CopyDisplayPath, FilesUi.Act(rev, "copy-display-path"), enabled: false, style: FilesUi.Ghost)
        ]);

        if (state.InoHandoffPrompt is { } handoff)
        {
            children.Add(MutedText(handoff));
        }

        if (state.PreviewApp is { } app)
        {
            children.Add(Card(app, "Smart App source folder")[
                FilesUi.Button("open-app", FilesCopy.OpenApp, FilesUi.Act(rev, "open-app"), style: FilesUi.Primary)
            ]);
        }
        else if (state.PreviewUrl is { } url)
        {
            var fileName = selected?.Item.Name ?? "";
            var mediaKind = FilesApp.IsAudio(fileName) ? "audio"
                : FilesApp.IsVideo(fileName) ? "video"
                : FilesApp.IsPdf(fileName) ? "pdf"
                : "image";
            var cardTitle = mediaKind == "image" ? (selected?.Item.Name ?? "Preview") : fileName;
            children.Add(VStack(Space.Xs)[
                Text(url).Tabular(),
                Text(mediaKind),
                Card(cardTitle, url)
            ]);
        }
        else if (state.Preview is { } text)
        {
            var fileName = selected?.Item.Name ?? "";
            var kind = FilesApp.IsMarkdown(fileName) ? "markdown" : (FilesApp.IsCode(fileName) ? "code" : "text");
            var previewNodes = new List<Node>
            {
                Surface(SurfaceKind.Recessed, padding: Space.Sm)[Text(text).Tabular()],
                MutedText(kind),
            };
            if (state.PreviewNote is { } note)
            {
                previewNodes.Add(MutedText(note));
            }

            children.Add(VStack(Space.Xs, previewNodes.ToArray()));
        }

        return Card(
            selected?.Item.Name ?? FilesCopy.QuickViewHeading,
            state.SelectedNeuronUri ?? FilesCopy.QuickViewHeading,
            [.. children]);
    }

    private static IEnumerable<FilesItem> Order(IEnumerable<FilesItem> items, string sort, bool descending)
    {
        var list = items.ToArray();
        return Sort(list.Where(item => item.Item.Kind == ItemKind.Folder), sort, descending)
            .Concat(Sort(list.Where(item => item.Item.Kind != ItemKind.Folder), sort, descending));
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

// The listing the screen derives from the state: the rows, selection and breadcrumbs it draws.
internal sealed record CollectionDefinition(
    CollectionItem[] Items,
    string? Selection = null,
    string? Cursor = null,
    string? Error = null,
    bool FileMode = true,
    bool SelectionEnabled = true,
    string? EmptyLabel = null,
    CollectionBreadcrumb[]? Breadcrumbs = null);

internal sealed record CollectionBreadcrumb(string Id, string Label);

internal sealed record CollectionItem(
    string Id,
    string Label,
    string Kind,
    string? SecondaryText = null,
    long? SizeBytes = null,
    DateTimeOffset? ModifiedAt = null,
    bool CanActivate = true,
    string? TrailingText = null);
