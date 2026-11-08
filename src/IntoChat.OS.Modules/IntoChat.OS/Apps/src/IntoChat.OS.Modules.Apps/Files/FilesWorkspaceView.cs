using System.Globalization;
using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using IntoChat.Files;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Collection;
using IntoChat.OS.Modules.Flutter.Image;
using IntoChat.OS.Modules.Flutter.InfoBar;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class FilesWorkspaceNeuron
{
    private async Task Render()
    {
        var files = State.Files;
        var screen = FilesScreen.Compose(files, State.Width, State.PreviewOpen, files.Revision, AppKey, State.PageOffset);
        await Push(screen);
        var rev = files.Revision.ToString(CultureInfo.InvariantCulture);
        DigitalBrain.UI.Node Button(string key, string text, string verb, bool enabled = true, int? index = null, string style = FilesUi.Secondary)
            => FilesUi.Button(key, text, FilesUi.Act(rev, verb, index), enabled, style);
        var views = await ReadOperations(Context.OperationsQuery, State.Started,
            error => Context.Services.GetRequiredService<ILogger<FilesWorkspaceNeuron>>().LogDebug(error, "A files operation could not be read."));
        var operations = OperationCards(State.Started, views, index => Button("cancel-" + index, "Cancel", "cancel", index: index));
        var notice = State.Notice ?? files.Message;
        var extras = FilesUi.Column(
        [
            .. notice is null ? Array.Empty<DigitalBrain.UI.Node>() : [FilesUi.Text(notice)],
            .. files.InoHandoffPrompt is { } handoffPrompt
                ? [FilesUi.Card("Ino Handoff", handoffPrompt, [FilesUi.Text(files.SelectedNeuronUri ?? ""), FilesUi.CopyButton("copy-ino-prompt", FilesCopy.CopyNeuronUri, files.SelectedNeuronUri ?? handoffPrompt)])]
                : Array.Empty<DigitalBrain.UI.Node>(),
            FilesUi.Field("input-name", AppKey + "/name"),
            FilesUi.Field("input-content", AppKey + "/content"),
            Button("write", "Save text file", "write", files.Breadcrumbs.LastOrDefault()?.Item.Flags.HasFlag(ItemFlags.Write) == true),
            Button("paste", files.Clipboard?.Move == true ? "Move here" : "Copy here", "paste", files.Clipboard is not null),
            FilesUi.Reference("confirmation", Id.Brain.Value + "/confirmations"),
            .. operations,
        ]);
        var main = FilesUi.Column([screen.Main, extras]);
        var full = FilesUi.Row([screen.Side, main], gap: 12, extents: [240, 0]);
        await View("side").Publish(files.Revision, new("Files", screen.Side));
        await View("main").Publish(files.Revision, new("Files", main));
        await View("full").Publish(files.Revision, new("Files", full));
        await View("view").Publish(files.Revision, new("Files", full));
    }

    // The operations query's view of each started operation (cross-silo); one that cannot be read is null.
    internal static async Task<OperationView?[]> ReadOperations(DigitalBrain.Spi.IOperationsQuery query, IReadOnlyList<FilesStartedOperation> started, Action<Exception> failed)
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

    // One card per started operation, from the operations query: the fraction while it runs, its result once done. An operation the
    // query does not know (not yet visible, or trimmed) shows as waiting while not done.
    internal static DigitalBrain.UI.Node[] OperationCards(IReadOnlyList<FilesStartedOperation> started, IReadOnlyList<OperationView?> views, Func<int, DigitalBrain.UI.Node> cancel)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(cancel);
        var cards = new DigitalBrain.UI.Node[started.Count];
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
            DigitalBrain.UI.Node[] children = running
                ? [FilesUi.Text(fraction.ToString("P0", CultureInfo.InvariantCulture)), cancel(index)]
                : [FilesUi.Text(status)];
            cards[index] = FilesUi.Card(operation.Label, status, children);
        }
        return cards;
    }

    private async Task Push(FilesScreen.Model screen)
    {
        try
        {
            var list = Get<ICollectionView>(new(Id.Brain, UIVocabulary.CollectionType, AppPath + "/items"));
            var current = await list.Read();
            try { await list.Set(screen.Files, current.Revision); }
            catch (RevisionConflictException)
            {
                current = await list.Read();
                await list.Set(screen.Files, current.Revision);
            }
            var bar = Get<IInfoBar>(new(Id.Brain, UIVocabulary.InfoBarType, AppPath + "/status"));
            if (State.Files.StatusTitle is { } title && !State.Files.Loading)
            {
                await bar.Show(State.Files.StatusSeverity, title, State.Files.StatusBody ?? "");
            }
            else { await bar.Dismiss(); }
            if (screen.ImageUrl is { } url)
            {
                await Get<IImage>(new(Id.Brain, UIVocabulary.ImageType, AppPath + "/preview")).Set(url, "image", State.Files.Selected?.Item.Name ?? "Preview");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Context.Services.GetRequiredService<ILogger<FilesWorkspaceNeuron>>().LogDebug(error, "Files child views were not updated.");
        }
    }
}
