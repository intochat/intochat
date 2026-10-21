using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client;

// `layout` (desktop or classic) chooses the root the shell's next connect shows; open views stay as they are.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var request in brain.On<AppRequested>(brain.Get<IApp>(Desktop.Key(brain)), brain.Stopping))
{
    if (request.Operation != "layout") { continue; }
    if (Desktop.ChosenLayout(request.Input) is not { } layout)
    { Console.Error.WriteLine("Desktop has no layout called " + request.Input); continue; }
    try { await Desktop.Layout(brain).Set(layout); }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not switch the layout: " + error.Message); }
}
