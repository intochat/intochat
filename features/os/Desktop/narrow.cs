using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// `narrow:<true|false>`: on a narrow screen only the focused window shows. The shell has no
// width hint yet, so this flag is the desktop's own state; the shell activates it below 900px.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (!clicked.Action.StartsWith("narrow:", StringComparison.Ordinal)) { continue; }
    try
    {
        await Desktop.Narrow(brain).Set(clicked.Action == "narrow:true" ? "true" : "false");
        await Desktop.Show(brain);
    }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not change its width mode: " + error.Message); }
}
