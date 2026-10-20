using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// The one startup signal: the desktop's `start` request composes the whole desktop.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Desktop.Key(brain));
var publisher = new ViewPublisher(brain.Get<IView>(Desktop.ViewKey(brain)), Desktop.Revision(brain));
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "start") { continue; }
    try { await Desktop.Start(brain, publisher); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException)
    { Console.Error.WriteLine("Desktop could not start: " + error.Message); }
}
