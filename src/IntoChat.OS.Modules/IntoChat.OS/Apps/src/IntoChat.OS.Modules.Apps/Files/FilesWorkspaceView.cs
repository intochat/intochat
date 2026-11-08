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
        string Action(string verb, int? index = null) => files.Revision.ToString(CultureInfo.InvariantCulture) + ":" + verb
            + (index is { } value ? ":" + value.ToString(CultureInfo.InvariantCulture) : "");
        UiNode Button(string key, string text, string verb, bool enabled = true, int? index = null, UiStyle style = UiStyle.Secondary)
            => Ui.Button(key, text, Action(verb, index), enabled, style);
        var views = await ReadOperations(Context.OperationsQuery, State.Started,
            error => Context.Services.GetRequiredService<ILogger<FilesWorkspaceNeuron>>().LogDebug(error, "A files operation could not be read."));
        var operations = OperationCards(State.Started, views, index => Button("cancel-" + index, "Cancel", "cancel", index: index));
        var notice = State.Notice ?? files.Message;
        var extras = Ui.Column("files-extras",
        [
            .. notice is null ? Array.Empty<UiNode>() : [Ui.Text("message", notice)],
            .. files.InoHandoffPrompt is { } handoffPrompt
                ? [Ui.Card("ino-handoff", "Ino Handoff", handoffPrompt, [Ui.Text("ino-handoff-uri", files.SelectedNeuronUri ?? ""), Ui.CopyButton("copy-ino-prompt", FilesCopy.CopyNeuronUri, files.SelectedNeuronUri ?? handoffPrompt)])]
                : Array.Empty<UiNode>(),
            Ui.Reference("input-name", new(UIVocabulary.TextFieldType, AppKey + "/name")),
            Ui.Reference("input-content", new(UIVocabulary.TextFieldType, AppKey + "/content")),
            Button("write", "Save text file", "write", files.Breadcrumbs.LastOrDefault()?.Item.Flags.HasFlag(ItemFlags.Write) == true),
            Button("paste", files.Clipboard?.Move == true ? "Move here" : "Copy here", "paste", files.Clipboard is not null),
            Ui.Reference("confirmations", new("confirmation", Id.Brain.Value + "/confirmations")),
            .. operations,
        ]);
        var main = Ui.Column("files-main-wrap", [screen.Main, extras]);
        var full = Ui.Row("files-full", [screen.Side, main], gap: 12, extents: [240, 0]);
        var version = Math.Max(1L, State.ViewVersion);
        try
        {
            await View("side").Publish(new(files.Revision, version, screen.Side));
            await View("main").Publish(new(files.Revision, version, main));
            await View("full").Publish(new(files.Revision, version, full));
            await View("view").Publish(new(files.Revision, version, full));
        }
        catch (InvalidOperationException)
        {
            var maxPublished = Math.Max(
                (await View("side").Read())?.Version ?? 0L,
                Math.Max(
                    Math.Max((await View("main").Read())?.Version ?? 0L, (await View("full").Read())?.Version ?? 0L),
                    (await View("view").Read())?.Version ?? 0L));
            var bumpedVersion = Math.Max(version, maxPublished) + 1L;
            await Save(State with { ViewVersion = bumpedVersion }, new FilesWorkspaceChanged(files.Revision));
            await View("side").Publish(new(files.Revision, bumpedVersion, screen.Side));
            await View("main").Publish(new(files.Revision, bumpedVersion, main));
            await View("full").Publish(new(files.Revision, bumpedVersion, full));
            await View("view").Publish(new(files.Revision, bumpedVersion, full));
        }
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
    internal static UiNode[] OperationCards(IReadOnlyList<FilesStartedOperation> started, IReadOnlyList<OperationView?> views, Func<int, UiNode> cancel)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(cancel);
        var cards = new UiNode[started.Count];
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
            UiNode[] children = running
                ? [Ui.Text("progress-" + index, fraction.ToString("P0", CultureInfo.InvariantCulture)), cancel(index)]
                : [Ui.Text("progress-" + index, status)];
            cards[index] = Ui.Card("operation-" + index, operation.Label, status, children);
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
