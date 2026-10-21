using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// More's `settings` opens the Settings app in a window beside the others.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (clicked.Action != "settings") { continue; }
    try { await Desktop.Open(brain, clicked.RequestId, "settings"); }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not open Settings: " + error.Message); }
}
