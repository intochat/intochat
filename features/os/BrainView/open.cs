using DigitalBrain.Apps;
using DigitalBrain.Client;

// "open": compose the brain scene and the panel, then put the scene on the stage with the panel docked.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(BrainView.Key(brain));
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    try
    {
        await BrainView.Compose(brain);
        await BrainView.Stage(brain).Go(BrainView.SceneKey(brain), BrainView.SurfaceKey(brain));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await app.Progress("attention", "The brain view could not compose its scene; open it again. " + error.Message); }
}
