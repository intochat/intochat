using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.TextField;

// The sidebar's buttons as state changes only. They never publish: each ends with a view-state write,
// the dirty signal view.cs (the single view writer) republishes on.
public sealed class InoChatCommands(IChatIndex index, IChat chat, ITextField rename, IText viewState, IText status,
    string dataKey, Func<Guid, Task> newConversation, Func<DateTimeOffset> now)
{
    public Func<Task>? Command(string action, Guid requestId)
    {
        var at = action.IndexOf(':', StringComparison.Ordinal);
        var (verb, id) = at < 0 ? (action, "") : (action[..at], action[(at + 1)..]);
        return verb switch
        {
            "chat" when ChatIndex.IsId(id) => () => Open(id, requestId),
            "rename" when ChatIndex.IsId(id) => () => BeginRename(id),
            "rename-save" => SaveRename,
            "rename-cancel" => () => Update(state => state with { RenamingId = null }),
            "archive" when ChatIndex.IsId(id) => () => Archive(id, requestId),
            "undo-archive" when ChatIndex.IsId(id) => () => Undo(id),
            _ => null,
        };
    }

    private Task Update(Func<InoViewState, InoViewState> change) => InoViewState.Update(viewState, change, now());

    private async Task Select(string agent, Guid operationId)
    {
        var selection = (await chat.Read()).Selection;
        if (selection?.Agent != agent) { await chat.SelectAgent(agent, selection?.Revision ?? 0, operationId); }
    }

    private async Task Open(string id, Guid operationId)
    {
        if (await index.Get(id) is not { Archived: false }) { return; }
        await Select(InoChats.AgentKey(dataKey, id), operationId);
        await InoViewState.MarkDirty(viewState, now());
    }

    private async Task BeginRename(string id)
    {
        if (await index.Get(id) is not { } entry) { return; }
        await rename.SetValue(entry.Title);
        await Update(state => state with { RenamingId = id });
    }

    private async Task SaveRename()
    {
        if ((await InoViewState.Read(viewState, now())).RenamingId is not { } id) { return; }
        try { await index.Rename(id, (await rename.Read()).Value); }
        catch (ArgumentException) { await status.Set(InoLayout.InvalidTitle); return; }
        var shown = await status.Read();
        if (shown.Markdown == InoLayout.InvalidTitle) { await status.TrySet("", shown.Version); }
        await Update(state => state.RenamingId == id ? state with { RenamingId = null } : state);
    }

    // The selection moves first: if that fails the chat stays open and unarchived; if the archive then
    // fails the person is simply on the next chat.
    private async Task Archive(string id, Guid operationId)
    {
        if ((await chat.Read()).Selection?.Agent == InoChats.AgentKey(dataKey, id))
        {
            var next = InoLayout.NextSelection((await index.List(false, 0, ChatIndex.MaxPage)).Entries, id);
            if (next is null) { await newConversation(operationId); }
            else { await Select(InoChats.AgentKey(dataKey, next), operationId); }
        }
        await index.Archive(id);
        await Update(state => state.Toasted(InoLayout.ArchivedToast, id, now()));
    }

    // Only the current toast's Undo restores; an expired or replaced toast's click changes nothing.
    private async Task Undo(string id)
    {
        if ((await InoViewState.Read(viewState, now())).UndoId != id) { return; }
        await index.Unarchive(id);
        await Update(state => state.UndoId == id ? state.Untoasted() : state);
    }
}
