using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;

// Open: connect the app's MCP server (its tools join the brain's search), then show the app's own web
// build in the declarative view surface.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(PhotoCraft.Key(brain));
await PhotoCraft.Compose(brain);
await Prepare(true);

await foreach (var request in brain.On(app, "open", brain.Stopping))
{ await Prepare(false); }

async Task Prepare(bool startup)
{
    try
    {
        var session = await PhotoCraft.Connect(brain, brain.Stopping);
        var current = await PhotoCraft.Surface(brain).Read();
        var previous = current?.Root.Children?.FirstOrDefault()?.Text ?? "";
        var connectionStatus = previous.StartsWith("PhotoCraft is not connected.", StringComparison.Ordinal)
            || previous.StartsWith("PhotoCraft tools connected", StringComparison.Ordinal)
            || previous.StartsWith("Could not open PhotoCraft.", StringComparison.Ordinal);
        if (!startup || current is null || connectionStatus)
        {
            var statusText = session.WebUrl is null
                ? "PhotoCraft tools connected; no page is served in this deployment."
                : "PhotoCraft tools connected.";
            await PhotoCraft.SetStatus(brain, statusText, session.WebUrl ?? "http://localhost:18082");
        }
        await PhotoCraft.Show(brain, session, brain.Stopping);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        await PhotoCraft.SetStatus(brain, PhotoCraft.Clip("Could not open PhotoCraft. Use Connect editor to retry. " + error.Message, PhotoCraft.MaxStatusChars));
    }
}
