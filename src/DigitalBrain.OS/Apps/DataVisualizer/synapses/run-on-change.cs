using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;

// A registry change means the set of views (or their definitions) changed: refresh them all.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var registry = brain.Get<IText>(appKey + "/views");

await foreach (var _ in brain.On<TextChanged>(registry, Refresh, brain.Stopping)) { await Refresh(); }
async Task Refresh() { await app.Request("refresh", ""); }
