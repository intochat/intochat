using DigitalBrain.Apps;
using DigitalBrain.Client;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Friends.Key(brain));
await foreach (var request in brain.On(app, "post", brain.Stopping))
{ await Friends.Post(brain, request.RequestId, request.Input); }
