using DigitalBrain.Apps;
using DigitalBrain.Client;

// "open": compose both scenes, make home the stage's floor and go there.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    await Ino.Compose(brain);
    await Ino.Stage(brain).SetHome(Ino.HomeKey(brain));
}
