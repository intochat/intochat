using DigitalBrain.Apps;
using DigitalBrain.Client;

// Compose the declared root at startup; workspace navigation owns visibility and focus.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
await Ino.Compose(brain);
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    try
    {
        await Ino.Compose(brain);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await app.Progress("attention", "Ino could not compose its scenes; open Ino again. " + error.Message); }
}
