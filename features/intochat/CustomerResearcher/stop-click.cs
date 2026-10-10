using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;

// The Stop button publishes the "stop" request.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
await foreach (var _ in brain.On<ButtonClicked>(brain.Get<IButton>(key + "/stop"), brain.Stopping))
{ await app.Request("stop", ""); }
