using DigitalBrain.Apps;
using DigitalBrain.Client;

// Open: connect the app's MCP server (its tools join the brain's search), then show the app's own web
// build in the shell's browser. Plain grain calls, safe to re-run.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(PhotoCraft.Key(brain));
await PhotoCraft.Compose(brain);
await Prepare(true);

await foreach (var request in brain.On(app, "open", brain.Stopping))
{ await Prepare(false); }

async Task Prepare(bool startup)
{
    var status = PhotoCraft.Status(brain);
    try
    {
        var session = await PhotoCraft.Connect(brain, brain.Stopping);
        var previous = await status.Read();
        var connectionStatus = previous.Markdown.StartsWith("PhotoCraft is not connected.", StringComparison.Ordinal)
            || previous.Markdown.StartsWith("PhotoCraft tools connected", StringComparison.Ordinal)
            || previous.Markdown.StartsWith("Could not open PhotoCraft.", StringComparison.Ordinal);
        if (!startup || previous.Version == 0 || connectionStatus)
        { await status.Set(session.WebUrl is null ? "PhotoCraft tools connected; no page is served in this deployment." : "PhotoCraft tools connected."); }
        await PhotoCraft.Show(brain, session, brain.Stopping);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await status.Set(PhotoCraft.Clip("Could not open PhotoCraft. Use Connect editor to retry. " + error.Message, PhotoCraft.MaxStatusChars)); }
}
