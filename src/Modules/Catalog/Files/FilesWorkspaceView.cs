using System.Globalization;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Log;
using DigitalBrain.Operations;
using DigitalBrain.UI;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.UI.BreadcrumbBar;
using IntoChat.OS.Modules.UI.ContextMenu;
using IntoChat.OS.Modules.UI.GridView;
using IntoChat.OS.UI;
using Microsoft.Extensions.Logging;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class FilesWorkspaceNeuron
{
    private async Task Render()
    {
        var files = State.Files;
        var screen = FilesScreen.Compose(
            files,
            State.Width,
            State.PreviewOpen,
            files.Revision,
            AppKey,
            State.PageOffset,
            State.ViewMode);
        var rev = files.Revision.ToString(CultureInfo.InvariantCulture);

        Node Button(string key, string text, string verb, bool enabled = true, int? index = null, string style = FilesUi.Secondary)
            => FilesUi.Button(key, text, FilesUi.Act(rev, verb, index), enabled, style);

        var views = await ReadOperations(
            operationsQuery,
            State.Started,
            error => logger.LogDebug(error, "A files operation could not be read."));
        var operations = OperationCards(State.Started, views, index => Button("cancel-" + index, "Cancel", "cancel", index: index));
        var notice = State.Notice ?? files.Message;
        var folderWritable = files.Breadcrumbs.LastOrDefault()?.Item.Flags.HasFlag(ItemFlags.Write) == true;

        var extras = VStack(
            Space.Md,
            [
                new NeuronRef("ui.dialog", FilesDialogKey),
                notice is not null
                    ? (Node)Banner("Files notice", notice, tone: "info")
                    : null,
                files.InoHandoffPrompt is { } handoffPrompt
                    ? Card("Ino Handoff", handoffPrompt)[
                        VStack(Space.Xs)[
                            MutedText(files.SelectedNeuronUri ?? "").Tabular(),
                            FilesUi.CopyButton("copy-ino-prompt", FilesCopy.CopyNeuronUri, files.SelectedNeuronUri ?? handoffPrompt)
                        ]
                    ].ToNode()
                    : null,
                Expander(
                    "Quick Write & Clipboard",
                    "Create a text file or paste clipboard selection into the active folder",
                    expanded: files.Clipboard is not null,
                    FilesUi.Field("input-name", AppKey + "/name", FieldKind.Text, "Single-segment file or folder name…", label: "File or folder name"),
                    FilesUi.Field("input-content", AppKey + "/content", FieldKind.Multiline, "File contents (max 256 KiB)…", label: "Contents"),
                    HStack(Space.Sm)[
                        Button("write", "Save text file", "write", folderWritable, style: FilesUi.Primary),
                        Button("paste", files.Clipboard?.Move == true ? "Move here" : "Copy here", "paste", files.Clipboard is not null)
                    ]),
                .. operations,
            ]).ToNode();

        await SyncFilesUiNeuronsAsync(files, folderWritable);

        var toolbar = HStack(Space.Md)[
            new NeuronRef("ui.breadcrumb", FilesBreadcrumbKey),
            new NeuronRef("ui.context-menu", FilesContextMenuKey)
        ];

        var main = string.Equals(State.ViewMode, "grid", StringComparison.OrdinalIgnoreCase)
            ? VStack(Space.Md, new NeuronRef("ui.grid-view", FilesGridKey), screen.Main, extras).ToNode()
            : VStack(Space.Md, screen.Main, extras).ToNode();
        var full = new AppShellNode(Key: "files-shell", Sidebar: screen.Side, Body: main, Toolbar: toolbar);

        // The workspace is host code: it publishes its views cleanly in-process, whoever's call led here.
        var edge = services?.GetService(typeof(DigitalBrain.Access.EdgeAuthority)) as DigitalBrain.Access.EdgeAuthority;
        using (edge?.EnterInProcess())
        {
            await View("side").Publish(files.Revision, new("Files", screen.Side));
            await View("main").Publish(files.Revision, new("Files", main));
            await View("full").Publish(files.Revision, new("Files", full));
            await View("view").Publish(files.Revision, new("Files", full));
        }
    }

    private async Task SyncFilesUiNeuronsAsync(FilesState files, bool folderWritable)
    {
        try
        {
            var crumbs = files.Breadcrumbs
                .Select((b, idx) => new BreadcrumbItem(idx.ToString(CultureInfo.InvariantCulture), string.IsNullOrWhiteSpace(b.Item.Name) ? "Root" : b.Item.Name))
                .ToArray();
            if (crumbs.Length == 0) { crumbs = [new BreadcrumbItem("0", "Workspace")]; }
            await Get<IBreadcrumbBar>(new DigitalBrain.NeuronId(Id.Brain, "ui.breadcrumb", FilesBreadcrumbKey)).SetItems(crumbs);

            await Get<IContextMenu>(new DigitalBrain.NeuronId(Id.Brain, "ui.context-menu", FilesContextMenuKey)).Configure("Actions",
            [
                new ContextMenuItem("mkdir", "New folder", "folder-plus", "Ctrl+Shift+N", Disabled: !folderWritable),
                new ContextMenuItem("rename", "Rename", "edit", "F2", Disabled: !folderWritable),
                new ContextMenuItem("copy", "Copy", "copy", "Ctrl+C"),
                new ContextMenuItem("delete", "Delete", "trash", "Del", Destructive: true, Disabled: !folderWritable),
            ]);

            if (string.Equals(State.ViewMode, "grid", StringComparison.OrdinalIgnoreCase))
            {
                var gridItems = files.Items.Take(48).Select((entry, i) => new GridViewItem(
                    i.ToString(CultureInfo.InvariantCulture),
                    entry.Item.Name,
                    entry.Item.Kind == ItemKind.Folder ? "Folder" : $"{entry.Item.Size ?? 0} B",
                    entry.Item.Kind == ItemKind.Folder ? "folder" : "file-text",
                    Badge: entry.Item.Kind == ItemKind.Folder ? "DIR" : null)).ToArray();
                await Get<IGridView>(new DigitalBrain.NeuronId(Id.Brain, "ui.grid-view", FilesGridKey)).Configure(
                    files.Breadcrumbs.LastOrDefault()?.Item.Name ?? "Files",
                    gridItems,
                    columns: 4);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogDebug(error, "Optional Modules.UI neuron sync skipped in minimal Files test host.");
        }
    }

    // The operations query's view of each started operation (cross-silo); one that cannot be read is null.
    internal static async Task<OperationView?[]> ReadOperations(
        IOperationsQuery query,
        IReadOnlyList<FilesStartedOperation> started,
        Action<Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(failed);
        var views = new OperationView?[started.Count];
        for (var i = 0; i < views.Length; i++)
        {
            try { views[i] = await query.Read(new OperationId(started[i].Id), CancellationToken.None); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<FilesWorkspaceNeuron>().LogSwallowed(error, "reading workspace file operation view");
                failed(error);
            }
        }

        return views;
    }

    // One card per started operation, from the operations query: the fraction while it runs, its result once done.
    internal static Node[] OperationCards(
        IReadOnlyList<FilesStartedOperation> started,
        IReadOnlyList<OperationView?> views,
        Func<int, Node> cancel)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(cancel);
        var cards = new Node[started.Count];
        for (var index = 0; index < cards.Length; index++)
        {
            var operation = started[index];
            var view = index < views.Count ? views[index] : null;
            var status = view?.Result switch
            {
                null => view is null ? (operation.Done ? "Finished" : "Waiting or working") : "Working",
                OperationResult.Succeeded => operation.Label == "Move to trash" ? "Moved to trash" : "Completed",
                OperationResult.Cancelled => "Cancelled",
                OperationResult.Planned => "Dry run complete. No files changed.",
                OperationResult.Failed failed => "Failed: " + failed.Code,
                _ => "Finished",
            };
            var running = view is null ? !operation.Done : view.Result is null;
            var fraction = Math.Clamp(view?.Fraction ?? 0, 0, 1);
            var statusKind = running
                ? StatusKind.Working
                : view?.Result is OperationResult.Failed
                    ? StatusKind.Error
                    : StatusKind.Done;
            Node[] children = running
                ?
                [
                    StatusPill(statusKind, status),
                    Text(fraction.ToString("P0", CultureInfo.InvariantCulture)).Tabular(),
                    cancel(index),
                ]
                :
                [
                    StatusPill(statusKind, status),
                    Text(status),
                ];
            cards[index] = Card(operation.Label, status, children);
        }

        return cards;
    }
}
