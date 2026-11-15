using System.Globalization;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using DigitalBrain.UI;
using IntoChat.Files;
using IntoChat.OS.Framework;
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
                notice is not null
                    ? (Node)Banner("Files notice", notice, tone: "info")
                    : null,
                notice is not null
                    ? Text(notice).ToNode()
                    : null,
                files.InoHandoffPrompt is { } handoffPrompt
                    ? Card("Ino Handoff", handoffPrompt)[
                        VStack(Space.Xs)[
                            MutedText(files.SelectedNeuronUri ?? "").Tabular(),
                            FilesUi.CopyButton("copy-ino-prompt", FilesCopy.CopyNeuronUri, files.SelectedNeuronUri ?? handoffPrompt)
                        ]
                    ].ToNode()
                    : null,
                Surface(SurfaceKind.Elevated, "Quick Write & Clipboard", "Create a text file or paste clipboard selection into the active folder")[
                    VStack(Space.Sm)[
                        FilesUi.Field("input-name", AppKey + "/name", FieldKind.Text, "Single-segment file or folder name…"),
                        FilesUi.Field("input-content", AppKey + "/content", FieldKind.Multiline, "File contents (max 256 KiB)…"),
                        HStack(Space.Sm)[
                            Button("write", "Save text file", "write", folderWritable, style: FilesUi.Primary),
                            Button("paste", files.Clipboard?.Move == true ? "Move here" : "Copy here", "paste", files.Clipboard is not null)
                        ]
                    ]
                ].ToNode(),
                Slot(Id.Brain.Value + "/confirmations", "confirmation"),
                .. operations,
            ]).ToNode();

        var main = VStack(Space.Md, screen.Main, extras).ToNode();
        var full = ResponsiveSplit(leading: screen.Side, trailing: main, leadingExtent: 216, gap: Space.Md);

        // The workspace is host code: it publishes its views as the platform of its brain, whoever's call led here.
        using (InProcessCallerScope.Enter())
        {
            await View("side").Publish(files.Revision, new("Files", screen.Side));
            await View("main").Publish(files.Revision, new("Files", main));
            await View("full").Publish(files.Revision, new("Files", full));
            await View("view").Publish(files.Revision, new("Files", full));
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
            catch (Exception error) when (error is not OperationCanceledException) { failed(error); }
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
