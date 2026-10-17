using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;

// A registry change means the set of views (or their definitions) changed: refresh them all.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await brain.React<TextChanged>(brain.Get<IText>(appKey + "/views"), _ => app.Request("refresh", ""), brain.Stopping);
