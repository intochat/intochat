using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Text;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Workspace.Key(brain));
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    try
    {
        await brain.Get<IText>(Workspace.Key(brain) + "/navigation-error").Set("");
        if (request.Operation == "navigate") { await Workspace.Navigate(brain, request.Input, request.RequestId.ToString()); }
        else if (request.Operation == "activate") { await Workspace.Activate(brain, request.Input, request.RequestId.ToString()); }
        else if (request.Operation == "open")
        {
            // Refresh presentation without recording a navigation mutation. A replay
            // must not reuse a receipt with a newer workspace snapshot.
            await Workspace.Compose(brain);
        }
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
    {
        // A rejected user action must not poison the durable navigation subscription.
        await brain.Get<IText>(Workspace.Key(brain) + "/navigation-error").Set("Could not open: " + error.Message);
        await Workspace.Compose(brain);
    }
}
