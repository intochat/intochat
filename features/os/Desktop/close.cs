using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Viewport;
using IntoChat.OS.Flutter.Views;

// `close:<windowId>/<index>` closes that tab; closing a window's last tab closes the window and clears its viewport.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (!clicked.Action.StartsWith("close:", StringComparison.Ordinal)) { continue; }
    var (id, index) = Desktop.WindowAndIndex(clicked.Action["close:".Length..]);
    try
    {
        var gone = false;
        await Desktop.Change(brain, clicked.RequestId, state =>
        {
            // The index names a visible tab; a stale click (tab or window already gone) does nothing.
            if (Desktop.TabAt(Desktop.Scope(brain), state, id, index) is not { } tab) { return Task.FromResult(new WindowChange(null, null)); }
            var change = WindowOps.CloseTab(state, id, tab);
            gone = change.Update?.Windows?.All(window => window.Id != id) == true;
            return Task.FromResult(change);
        });
        if (!gone) { continue; }
        var viewport = brain.Get<IViewport>(Desktop.Key(brain) + "/window/" + id);
        await viewport.Set(null, (await viewport.Read()).Revision);
    }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not close " + clicked.Action + ": " + error.Message); }
}
