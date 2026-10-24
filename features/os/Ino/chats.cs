using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.TextField;
using IntoChat.OS.Flutter.Views;

// The sidebar's buttons: open a chat, rename it in a modal, archive it with Undo. Each click is handled once.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var data = Ino.DataKey(brain);
var index = InoChats.Index(brain);
var chat = brain.Get<IChat>(data + "/transcript");
var rename = brain.Get<ITextField>(data + "/rename");
var view = brain.Get<IView>(Ino.Key(brain) + "/view");
var ledger = await RequestLedger.LoadAsync(brain, "chats");
await foreach (var clicked in brain.On<ViewAction>(view, brain.Stopping))
{
    if (clicked.RequestId == Guid.Empty || ledger.Handled(clicked.RequestId)) { continue; }
    var (verb, id) = Split(clicked.Action);
    Func<Task>? handle = verb switch
    {
        "chat" when ChatIndex.IsId(id) => () => Open(id, clicked.RequestId),
        "rename" when ChatIndex.IsId(id) => () => BeginRename(id),
        "rename-save" => SaveRename,
        "rename-cancel" => () => Update(state => state with { RenamingId = null }),
        "archive" when ChatIndex.IsId(id) => () => Archive(id, clicked.RequestId),
        "undo-archive" when ChatIndex.IsId(id) => () => Undo(id),
        _ => null,
    };
    if (handle is null) { continue; }
    try { await handle(); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { await Ino.Status(brain, "Couldn't update the chat. Try again."); }
    await ledger.Record(clicked.RequestId);
}

static (string Verb, string Id) Split(string action)
{
    var at = action.IndexOf(':', StringComparison.Ordinal);
    return at < 0 ? (action, "") : (action[..at], action[(at + 1)..]);
}

async Task Update(Func<InoViewState, InoViewState> change)
{
    await InoViewState.Save(brain, change((await InoViewState.Load(brain)).Current(DateTimeOffset.UtcNow)));
    await Ino.PublishView(brain);
}

async Task Select(string agent, Guid operationId)
{
    var selection = await Ino.EnsureSelection(brain);
    if (selection.Agent != agent) { await chat.SelectAgent(agent, selection.Revision, operationId); }
}

async Task Open(string id, Guid operationId)
{
    if (await index.Get(id) is not { Archived: false }) { return; }
    await Select(InoChats.AgentKey(data, id), operationId);
    await Ino.PublishView(brain);
}

async Task BeginRename(string id)
{
    if (await index.Get(id) is not { } entry) { return; }
    await rename.SetValue(entry.Title);
    await Update(state => state with { RenamingId = id });
}

async Task SaveRename()
{
    if ((await InoViewState.Load(brain)).RenamingId is not { } id) { return; }
    try { await index.Rename(id, (await rename.Read()).Value); }
    catch (ArgumentException) { await Ino.Status(brain, InoLayout.InvalidTitle); return; }
    if ((await brain.Get<IText>(data + "/status").Read()).Markdown == InoLayout.InvalidTitle) { await Ino.Status(brain, ""); }
    await Update(state => state with { RenamingId = null });
}

// Archiving the open chat moves the selection to the newest remaining chat, or a new one.
async Task Archive(string id, Guid operationId)
{
    await index.Archive(id);
    if ((await Ino.EnsureSelection(brain)).Agent == InoChats.AgentKey(data, id))
    {
        var next = InoLayout.NextSelection((await index.List(false, 0, ChatIndex.MaxPage)).Entries, id);
        if (next is null) { await Ino.NewConversation(brain, operationId); }
        else { await Select(InoChats.AgentKey(data, next), operationId); }
    }
    await Update(state => state.Toasted(InoLayout.ArchivedToast, id, DateTimeOffset.UtcNow));
}

// Undo restores the entry only; the selection stays where it is.
async Task Undo(string id)
{
    await index.Unarchive(id);
    await Update(state => state.UndoId == id ? state with { Toast = null, UndoId = null, ToastAt = null } : state);
}
