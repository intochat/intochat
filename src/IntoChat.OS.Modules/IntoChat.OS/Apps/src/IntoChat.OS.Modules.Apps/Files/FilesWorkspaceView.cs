using System.Globalization;
using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using IntoChat.Files;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Collection;
using IntoChat.OS.Modules.Flutter.Image;
using IntoChat.OS.Modules.Flutter.InfoBar;
using IntoChat.OS.Modules.Flutter.Progress;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class FilesWorkspaceNeuron
{
    private async Task Render()
    {
        var files = State.Files;
        var screen = FilesScreen.Compose(files, State.Width, State.PreviewOpen, files.Revision);
        await Push(screen);
        string Action(string verb, int? index = null) => files.Revision.ToString(CultureInfo.InvariantCulture) + ":" + verb
            + (index is { } value ? ":" + value.ToString(CultureInfo.InvariantCulture) : "");
        UiNode Button(string key, string text, string verb, bool enabled = true, int? index = null, UiStyle style = UiStyle.Secondary)
            => Ui.Button(key, text, Action(verb, index), enabled, style);
        var operations = State.Operations.Select((operation, index) => Ui.Card("operation-" + index, operation.Label, operation.Status,
            [Ui.Text("progress-" + index, operation.Done ? operation.Status : operation.Progress.ToString("P0", CultureInfo.InvariantCulture)),
                .. operation.Done ? [] : new[] { Button("cancel-" + index, "Cancel", "cancel", index: index) }])).ToArray();
        var notice = State.Notice ?? files.Message;
        var extras = Ui.Column("files-extras",
        [
            .. notice is null ? Array.Empty<UiNode>() : [Ui.Text("message", notice)],
            Ui.Reference("input-name", new(UIVocabulary.TextFieldType, AppKey + "/name")),
            Ui.Reference("input-content", new(UIVocabulary.TextFieldType, AppKey + "/content")),
            Button("write", "Save text file", "write", files.Breadcrumbs.LastOrDefault()?.Item.Flags.HasFlag(ItemFlags.Write) == true),
            Button("paste", files.Clipboard?.Move == true ? "Move here" : "Copy here", "paste", files.Clipboard is not null),
            Ui.Reference("confirmations", new("confirmation", Id.Brain.Value + "/confirmations")),
            .. operations,
        ]);
        var main = Ui.Column("files-main-wrap", [screen.Main, extras]);
        await View("side").Publish(new(files.Revision, State.ViewVersion, screen.Side));
        await View("main").Publish(new(files.Revision, State.ViewVersion, main));
        await View("full").Publish(new(files.Revision, State.ViewVersion, Ui.Column("files-full", [screen.Side, main], extents: [260, 1])));
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
            if (State.Files.Loading)
            {
                await Get<IProgress>(new(Id.Brain, UIVocabulary.ProgressType, AppPath + "/loading")).Set(false, 0, FilesCopy.Loading);
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
