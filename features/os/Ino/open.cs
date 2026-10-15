using DigitalBrain.Apps;
using DigitalBrain.Client;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
await Ino.ComposeAsync(brain);
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    try { await Ino.ComposeAsync(brain); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        await app.FailPresentation(Ino.ProgramRevision(brain), error.Message);
        await Ino.Status(brain, "Could not open Ino: " + Ino.Clip(error.Message));
    }
}
