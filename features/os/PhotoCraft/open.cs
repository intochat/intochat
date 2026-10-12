using DigitalBrain.Apps;
using DigitalBrain.Client;

// Open: connect the app's MCP server (its tools join the brain's search), then show the app's own web
// build in the shell's browser. Plain grain calls, safe to re-run.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(PhotoCraft.Key(brain));
await PhotoCraft.Compose(brain);
if ((await PhotoCraft.Status(brain).Read()).Version == 0)
{ await PhotoCraft.Status(brain).Set("PhotoCraft is not connected. Request open to connect its tools."); }

await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    var status = PhotoCraft.Status(brain);
    try
    {
        var session = await PhotoCraft.Connect(brain, brain.Stopping);
        await status.Set(session.WebUrl is null ? "PhotoCraft tools connected; no page is served in this deployment." : "PhotoCraft tools connected.");
        await PhotoCraft.Show(brain, session, brain.Stopping);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await status.Set(PhotoCraft.Clip(error.Message, PhotoCraft.MaxStatusChars)); }
}
