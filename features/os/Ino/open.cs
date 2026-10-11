using DigitalBrain.Apps;
using DigitalBrain.Client;

// "open": compose both scenes, make home the stage's floor and go there.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    try
    {
        await Ino.Compose(brain);
        await Ino.Stage(brain).SetHome(Ino.HomeKey(brain));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await app.Progress("attention", "Ino could not compose its scenes; open Ino again. " + error.Message); }
}
