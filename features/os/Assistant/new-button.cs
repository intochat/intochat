using DigitalBrain.Apps;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;

// The New conversation button: the click becomes a request, and new.cs does the work.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Assistant.Key(brain));
await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(Assistant.Key(brain) + "/new"), brain.Stopping))
{ if (clicked.RequestId != Guid.Empty) { await app.Request("new", ""); } }
