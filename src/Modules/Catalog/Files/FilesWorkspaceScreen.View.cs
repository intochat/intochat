using System.Globalization;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.UI;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class FilesWorkspaceScreen
{
    protected override View Render(FilesWorkspaceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var files = state.Files;
        var appKey = AppKey;
        var screen = FilesScreen.Compose(files, state.Width, state.PreviewOpen, files.Revision, appKey, state.PageOffset, state.ViewMode);
        var rev = files.Revision.ToString(CultureInfo.InvariantCulture);

        Node Button(string key, string text, string verb, bool enabled = true, int? index = null, string style = FilesUi.Secondary)
            => FilesUi.Button(key, text, FilesUi.Act(rev, verb, index), enabled, style);

        var notice = state.Notice ?? files.Message;
        var folderWritable = files.Breadcrumbs.LastOrDefault()?.Item.Flags.HasFlag(ItemFlags.Write) == true;

        var extras = VStack(
            Space.Md,
            [
                Prompt(state, files, appKey, rev),
                notice is not null ? (Node)Banner("Files notice", notice, tone: "info") : null,
                files.InoHandoffPrompt is { } handoffPrompt
                    ? Card("Ino Handoff", handoffPrompt)[
                        VStack(Space.Xs)[
                            MutedText(files.SelectedNeuronUri ?? "").Tabular(),
                            FilesUi.CopyButton("copy-ino-prompt", FilesCopy.CopyNeuronUri, files.SelectedNeuronUri ?? handoffPrompt)
                        ]
                    ].ToNode()
                    : null,
                Editor(files, state.Typed, appKey, rev),
                Expander(
                    "Quick Write & Clipboard",
                    "Create a text file or paste clipboard selection into the active folder",
                    expanded: files.Clipboard is not null,
                    FilesUi.Field("input-name", appKey + "/" + NameField, FieldKind.Text, "Single-segment file or folder name…", label: "File or folder name", value: state.Typed.Name ?? "", typed: NameField),
                    FilesUi.Field("input-content", appKey + "/" + ContentField, FieldKind.Multiline, "File contents (max 256 KiB)…", label: "Contents", value: state.Typed.Content ?? "", typed: ContentField),
                    HStack(Space.Sm)[
                        Button("write", "Save text file", "write", folderWritable, style: FilesUi.Primary),
                        Button("paste", files.Clipboard?.Move == true ? "Move here" : "Copy here", "paste", files.Clipboard is not null)
                    ]),
                .. OperationCards(state.Started, index => Button("cancel-" + index, "Cancel", "cancel", index: index)),
            ]).ToNode();

        var main = VStack(Space.Md, [screen.Main, extras]).ToNode();
        return new View("Files", new AppShellNode(Key: "files-shell", Sidebar: screen.Side, Body: main));
    }

    // The inline prompt for a new folder or a rename that came without a name.
    private static Node? Prompt(FilesWorkspaceState state, FilesState files, string appKey, string rev)
    {
        if (state.Prompt is not { } operation) { return null; }
        var mkdir = operation == "mkdir";
        return Card(
            mkdir ? "New folder" : "Rename item",
            mkdir
                ? "Enter a single-segment folder name to create in the current directory."
                : $"Enter a new name for '{files.Selected?.Item.Name ?? "selected item"}'.")[
            VStack(Space.Sm)[
                FilesUi.Field("prompt-input", appKey + "/" + PromptField, FieldKind.Text, mkdir ? "e.g. reports" : files.Selected?.Item.Name ?? "", label: "Name", value: state.Typed.Prompt ?? "", typed: PromptField),
                HStack(Space.Sm)[
                    FilesUi.Button("prompt-confirm", mkdir ? "Create" : "Rename", FilesUi.Act(rev, "prompt-confirm"), style: FilesUi.Primary),
                    FilesUi.Button("prompt-cancel", "Cancel", FilesUi.Act(rev, "prompt-cancel"), style: FilesUi.Ghost)
                ]
            ]
        ].ToNode();
    }

    // The selected text file in an editable field with Save, when the folder takes writes.
    private static Node? Editor(FilesState files, FilesTyped typed, string appKey, string rev)
    {
        if (Editable(files) is not { } text || files.Selected is not { } selected) { return null; }
        return Card("Edit " + selected.Item.Name, "Changes are saved as a new version of this file")[
            VStack(Space.Sm)[
                FilesUi.Field("editor-content", appKey + "/" + EditorField, FieldKind.Multiline, "File contents…", label: "Contents", value: typed.Editor ?? text, typed: EditorField),
                HStack(Space.Sm)[
                    FilesUi.Button("save-edit", "Save", FilesUi.Act(rev, "save-edit"), style: FilesUi.Primary)
                ]
            ]
        ].ToNode();
    }

    // One card per started operation, from what its signals reported: the fraction while it runs, its status once done.
    internal static Node[] OperationCards(IReadOnlyList<FilesStartedOperation> started, Func<int, Node> cancel)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(cancel);
        var cards = new Node[started.Count];
        for (var index = 0; index < cards.Length; index++)
        {
            var operation = started[index];
            if (!operation.Done)
            {
                cards[index] = Card(operation.Label, "Working", [
                    StatusPill(StatusKind.Working, "Working"),
                    Text(operation.Fraction.ToString("P0", CultureInfo.InvariantCulture)).Tabular(),
                    cancel(index),
                ]);
                continue;
            }
            var status = operation.Status ?? "Finished";
            cards[index] = Card(operation.Label, status, [
                StatusPill(operation.Failed ? StatusKind.Error : StatusKind.Done, status),
                Text(status),
            ]);
        }

        return cards;
    }
}
