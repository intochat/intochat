using DigitalBrain.Apps;
using DigitalBrain.Client;

// Compose the declared root at startup; workspace navigation owns visibility and focus.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(BrainView.Key(brain));
await BrainView.Compose(brain);
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    try
    {
        await BrainView.Compose(brain);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await app.Progress("attention", "The brain view could not compose its scene; open it again. " + error.Message); }
}
