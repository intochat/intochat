using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// `open:<app>` opens the app in a new window beside the others (or focuses the tab already showing it).
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (!clicked.Action.StartsWith("open:", StringComparison.Ordinal)) { continue; }
    var name = clicked.Action["open:".Length..];
    try { await Desktop.Open(brain, clicked.RequestId, name); }
    // One bad click must not end the subscription.
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not open " + name + ": " + error.Message); }
}
