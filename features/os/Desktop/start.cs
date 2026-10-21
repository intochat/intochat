using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client;

// The one startup signal: the desktop's `start` request composes the whole desktop.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Desktop.Key(brain));
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "start") { continue; }
    // A failed first run (e.g. Ino busy) is retried next start and never skips the desktop itself.
    try { await Desktop.FirstRun(brain); }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not greet: " + error.Message); }
    try { await Desktop.Start(brain); }
    // One bad request must not end the only startup subscription.
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not start: " + error); }
}
