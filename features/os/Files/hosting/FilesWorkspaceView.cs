using System.Globalization;
using DigitalBrain.Capabilities.Storage;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class FilesWorkspaceNeuron
{
    private async Task Render()
    {
        var files = State.Files;
        string Action(string verb, int? index = null) => files.Revision.ToString(CultureInfo.InvariantCulture) + ":" + verb
            + (index is { } value ? ":" + value.ToString(CultureInfo.InvariantCulture) : "");
        UiNode Button(string key, string text, string verb, bool enabled = true, int? index = null, UiStyle style = UiStyle.Secondary)
            => Ui.Button(key, text, Action(verb, index), enabled, style);
        UiNode Input(string key) => Ui.Reference("input-" + key, new(UIVocabulary.TextFieldType, AppKey + "/" + key));

        var storages = files.Storages.Select((storage, index) => Ui.Card("storage-" + index, storage.Label, storage.Error ?? storage.Kind,
            [Button("storage-open-" + index, "Browse", "storage", storage.Error is null, index)])).ToArray();
        var side = Ui.Column("files-side",
            [Ui.Text("storages-title", "Storages"), .. storages,
                .. files.Storages.Length == 0 ? new[] { Ui.Text("storage-empty", "No storage is connected. Add one in Settings.") } : []]);
        await View("side").Publish(new(files.Revision, State.ViewVersion, side));

        var selected = files.Selected;
        var folderFlags = files.Breadcrumbs.LastOrDefault()?.Item.Flags ?? ItemFlags.None;
        var rows = files.Items.Skip(State.PageOffset).Take(40).Select((entry, row) =>
        {
            var index = State.PageOffset + row;
            var selectedHere = selected?.Connection == entry.Connection && selected.Item.Id == entry.Item.Id;
            var label = entry.Item.Kind == ItemKind.Folder ? "Folder" : entry.Item.Size is { } bytes ? bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes" : "File";
            var storage = files.Storages.FirstOrDefault(item => item.Connection == entry.Connection)?.Label ?? "Storage";
            return Ui.Row("row-" + index,
                [Button("item-" + index, entry.Item.Name, "select", index: index, style: selectedHere ? UiStyle.Primary : UiStyle.Ghost), Ui.Text("detail-" + index, storage + " · " + label)]);
        }).ToArray();
        var operations = State.Operations.Select((operation, index) => Ui.Card("operation-" + index, operation.Label, operation.Status,
            [Ui.Text("progress-" + index, operation.Done ? operation.Status : operation.Progress.ToString("P0", CultureInfo.InvariantCulture)),
                .. operation.Done ? [] : new[] { Button("cancel-" + index, "Cancel", "cancel", index: index) }])).ToArray();
        var preview = files.Preview is { } content ? new[] { Ui.Card("preview", selected?.Item.Name ?? "Preview", content) } : [];
        var notice = State.Notice ?? files.Message;
        var main = Ui.Column("files-main",
        [
            Ui.Row("navigation", [Button("storages", "Storages", "open"), Button("up", "Up", "up", files.Connection is not null)]),
            Ui.Row("breadcrumbs", files.Breadcrumbs.Select((item, index) => Button("crumb-" + index, item.Item.Name, "breadcrumb", index: index)).ToArray()),
            Input("search"), Ui.Row("search-actions", [Button("search-here", "Search here", "search", files.Connection is not null), Button("search-all", "Search all storages", "search-all")]),
            .. notice is null ? [] : new[] { Ui.Text("message", notice) },
            Ui.List("items", rows),
            Ui.Text("page-label", files.Items.Length == 0 ? "No items" : $"{State.PageOffset + 1}–{Math.Min(State.PageOffset + 40, files.Items.Length)} of {files.Items.Length} loaded"),
            Ui.Row("paging", [Button("previous", "Previous", "previous", State.PageOffset > 0), Button("next", "Next", "next", State.PageOffset + 40 < files.Items.Length),
                Button("more", "Load more", "more", files.Pages.Any(page => !page.Complete))]),
            Ui.Row("selection", [Button("preview-open", selected?.Item.Kind == ItemKind.Folder ? "Open folder" : "Preview", "preview", selected is not null),
                Button("copy", "Copy", "copy", selected?.Item.Flags.HasFlag(ItemFlags.Copy) == true), Button("move", "Move", "move", selected?.Item.Flags.HasFlag(ItemFlags.Move) == true),
                Button("trash", "Move to trash", "delete", selected?.Item.Flags.HasFlag(ItemFlags.Trash) == true, style: UiStyle.Danger)]),
            .. preview,
            Input("name"), Input("content"),
            Ui.Row("write-actions", [Button("mkdir", "New folder", "mkdir", folderFlags.HasFlag(ItemFlags.CreateFolder)), Button("write", "Save text file", "write", folderFlags.HasFlag(ItemFlags.Write)),
                Button("rename", "Rename selected", "rename", selected?.Item.Flags.HasFlag(ItemFlags.Rename) == true),
                Button("paste", files.Clipboard?.Move == true ? "Move here" : "Copy here", "paste", files.Clipboard is not null && folderFlags.HasFlag(ItemFlags.Write))]),
            Ui.Menu("conflicts", "If a name exists: " + (files.Conflict == ConflictPolicy.Fail ? "Stop" : files.Conflict == ConflictPolicy.Rename ? "Keep both" : "Replace"),
                [Button("conflict-fail", "Stop without changes", "fail"), Button("conflict-rename", "Keep both", "keep-both"), Button("conflict-overwrite", "Replace after confirmation", "replace", style: UiStyle.Danger)]),
            Button("dry-run", files.DryRun ? "Dry run enabled" : "Enable dry run", "dry-run", style: files.DryRun ? UiStyle.Primary : UiStyle.Secondary),
            Ui.Reference("confirmations", new("confirmation", Id.Brain.Value + "/confirmations")),
            .. operations,
        ]);
        await View("main").Publish(new(files.Revision, State.ViewVersion, main));
        await View("full").Publish(new(files.Revision, State.ViewVersion, Ui.Column("files-full", [side, main])));
    }
}
