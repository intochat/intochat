using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// `tab:<windowId>/<index>` selects a tab; `newtab` (dock: the focused window) and `newtab:<windowId>`
// (a window's +) open the person's New tab app, Ino by default, as a new tab.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    var select = clicked.Action.StartsWith("tab:", StringComparison.Ordinal);
    if (!select && clicked.Action != "newtab" && !clicked.Action.StartsWith("newtab:", StringComparison.Ordinal)) { continue; }
    try
    {
        if (select)
        {
            var (id, index) = Desktop.WindowAndIndex(clicked.Action["tab:".Length..]);
            await Desktop.Change(brain, clicked.RequestId, state => Task.FromResult(Desktop.TabAt(Desktop.Scope(brain), state, id, index) is { } tab
                ? WindowOps.SelectTab(state, id, tab) : new WindowChange(null, null)));
            continue;
        }
        var window = clicked.Action == "newtab" ? null : clicked.Action["newtab:".Length..];
        var name = await Desktop.NewTabApp(brain);
        if (await Desktop.AppView(brain, name) is not { } view) { continue; }
        await Desktop.Change(brain, clicked.RequestId, state => Task.FromResult(WindowOps.NewTab(state, window, name, view)));
    }
    // One bad click must not end the subscription.
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not " + clicked.Action + ": " + error.Message); }
}
