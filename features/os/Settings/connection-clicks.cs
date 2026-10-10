using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;

// The Refresh connections button posts the refresh request.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await foreach (var _ in brain.On<ButtonClicked>(brain.Get<IButton>(appKey + "/refresh-connections"), brain.Stopping))
{ await app.Request("refresh-connections", ""); }
