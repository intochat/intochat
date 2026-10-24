using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Views;

// What the surface shows besides its durable references. Chats are the unarchived index entries.
public sealed record InoViewModel(IReadOnlyList<ChatIndexEntry> Chats, string SelectedId, string Query,
    UiNode? ModelMenu = null, string? RenamingId = null, string? Toast = null, string? UndoId = null, UiNode? FirstRun = null);

// Pure composition: durable references retain their public contracts and local UI identity. Node keys use
// row indexes, never chat ids; ids travel only in button actions.
public static class InoView
{
    public static UiNode Render(string appKey, InoViewModel model, bool narrow = false)
    {
        UiNode[] children = [narrow ? Rail(model) : Sidebar(appKey, model), Surface(appKey, model)];
        List<double> extents = [narrow ? InoLayout.RailWidth : InoLayout.SidebarWidth, 0];
        if (model.RenamingId is not null) { children = [.. children, Rename(appKey)]; extents.Add(InoLayout.OverlayExtent); }
        if (model.Toast is { Length: > 0 } toast) { children = [.. children, Toast(toast, model.UndoId)]; extents.Add(InoLayout.OverlayExtent); }
        return Ui.Row("ino", children, gap: 0, extents: [.. extents]);
    }

    private static UiNode Sidebar(string appKey, InoViewModel model)
    {
        var shown = InoLayout.Filter(InoLayout.Newest(model.Chats), model.Query);
        var history = shown.Count > 0
            ? Ui.List("chats", [.. shown.Select((chat, i) => Row(chat, i, chat.Id == model.SelectedId))])
            : Ui.Text("chats-empty", model.Query.Trim().Length > 0 ? InoLayout.NoMatchCopy(model.Query) : InoLayout.EmptyCopy);
        return Ui.Column("sidebar",
        [
            Ui.Button("new", "New chat", "new", style: UiStyle.Primary, icon: "plus"),
            Ui.Reference("search", new(UIVocabulary.TextFieldType, appKey + "/chat-search")),
            history,
        ], extents: [40, InoLayout.SearchHeight, 0]);
    }

    private static UiNode Row(ChatIndexEntry chat, int i, bool selected) => Ui.Row("chat-" + i,
    [
        Ui.Button("open-" + i, chat.Title, "chat:" + chat.Id, style: selected ? UiStyle.Secondary : UiStyle.Ghost),
        Ui.Menu("menu-" + i, "Chat actions",
        [
            Ui.Button("rename-" + i, "Rename", "rename:" + chat.Id, icon: "edit"),
            Ui.Button("archive-" + i, "Archive", "archive:" + chat.Id, icon: "archive"),
        ], icon: "more"),
    ], extents: [0, 32]);

    // Below the breakpoint: New chat and a history menu only.
    private static UiNode Rail(InoViewModel model) => Ui.Column("rail",
    [
        Ui.IconButton("new", "plus", "New chat", "new"),
        Ui.Menu("history", "Chats",
            [.. InoLayout.Newest(model.Chats).Select((chat, i) => Ui.Button("open-" + i, chat.Title, "chat:" + chat.Id,
                style: chat.Id == model.SelectedId ? UiStyle.Secondary : UiStyle.Ghost))], icon: "list"),
    ], extents: [40, 40]);

    private static UiNode Surface(string appKey, InoViewModel model)
    {
        UiNode status = Ui.Reference("status", new(UIVocabulary.TextType, appKey + "/status"));
        var header = model.ModelMenu is { } menu
            ? Ui.Row("header", [status, menu], extents: [0, 160])
            : Ui.Row("header", [status], extents: [0]);
        var transcript = Ui.Reference("transcript", new(UIVocabulary.ChatType, appKey + "/transcript"));
        var composer = Ui.Reference("composer", new(UIVocabulary.ComposerType, appKey + "/composer"));
        return model.FirstRun is { } card
            ? Ui.Column("main", [header, card, transcript, composer], extents: [InoLayout.HeaderHeight, InoLayout.FirstRunHeight, 0, InoLayout.ComposerHeight])
            : Ui.Column("main", [header, transcript, composer], extents: [InoLayout.HeaderHeight, 0, InoLayout.ComposerHeight]);
    }

    private static UiNode Rename(string appKey) => Ui.Modal("rename", "Rename chat", null,
    [
        Ui.Reference("rename-field", new(UIVocabulary.TextFieldType, appKey + "/rename")),
        Ui.Button("rename-save", "Save", "rename-save", style: UiStyle.Primary),
        Ui.Button("rename-cancel", "Cancel", "rename-cancel", style: UiStyle.Ghost),
    ]);

    private static UiNode Toast(string text, string? undoId) => Ui.Toast("toast", text, UiTone.Neutral,
        undoId is null ? null : Ui.Button("undo", "Undo", "undo-archive:" + undoId, style: UiStyle.Link));
}
