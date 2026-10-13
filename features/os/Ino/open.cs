using DigitalBrain.Apps;
using DigitalBrain.Client;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
await Present();
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    await Present();
}

async Task Present()
{
    try { await Ino.Present(brain); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await app.FailPresentation(Ino.ProgramRevision(brain), "Ino could not publish its presentation. " + error.Message); }
}
