using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// `focus:<windowId>` focuses a window's active tab (on narrow screens the window fills the screen).
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (!clicked.Action.StartsWith("focus:", StringComparison.Ordinal)) { continue; }
    var id = clicked.Action["focus:".Length..];
    try { await Desktop.Change(brain, clicked.RequestId, state => Task.FromResult(WindowOps.Focus(state, id))); }
    // One bad click must not end the subscription.
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not focus " + id + ": " + error.Message); }
}
