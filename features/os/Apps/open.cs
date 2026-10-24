using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client;

// Startup shows the durable target and presents the view. `open` takes "" (the library),
// "app:<publisher>/<app>[@<version>]" or "installed:<name>"; anything malformed opens the library.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Apps.Key(brain));
await Compose();

await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "open") { continue; }
    try
    {
        await Apps.Open(Apps.ViewState(brain), request.Input);
        await Compose();
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine("Apps could not open: " + error.GetType().Name); }
}

async Task Compose()
{
    // Present even if the first render fails: the next action renders again.
    try { await Apps.Show(brain); }
    finally { await Apps.Present(brain); }
}
