using DigitalBrain.Apps;
using DigitalBrain.Client;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Friends.Key(brain));
await Friends.Present(brain);
await foreach (var request in brain.On(app, "open", brain.Stopping))
{ await Friends.Present(brain); }
