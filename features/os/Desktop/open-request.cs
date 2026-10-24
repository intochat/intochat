using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client;

// `open` (the app's name) is the Workspace app handing the desktop a window to place, as a workspace activation
// does for Settings, an Apps detail and a finished install: the same path as the dock's `open:<app>`.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var request in brain.On<AppRequested>(brain.Get<IApp>(Desktop.Key(brain)), brain.Stopping))
{
    if (request.Operation != "open" || string.IsNullOrWhiteSpace(request.Input)) { continue; }
    try { await Desktop.Open(brain, request.RequestId, request.Input.Trim()); }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not open " + request.Input + ": " + error.Message); }
}
