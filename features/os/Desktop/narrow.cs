using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Client;

// `narrow` (true or false): the shell's width hint. Below 900 logical pixels it sends true and
// only the focused window shows; false when the window is wide again.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var request in brain.On<AppRequested>(brain.Get<IApp>(Desktop.Key(brain)), brain.Stopping))
{
    if (request.Operation != "narrow") { continue; }
    if (request.Input is not ("true" or "false"))
    { Console.Error.WriteLine("Desktop's width hint is true or false, not " + request.Input); continue; }
    try
    {
        // Only a change re-renders: the shell reports on every connect.
        if ((await Desktop.Narrow(brain).Read()).Markdown == request.Input) { continue; }
        await Desktop.Narrow(brain).Set(request.Input);
        await Desktop.Show(brain);
    }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not change its width mode: " + error.Message); }
}
