using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var registry = brain.Get<IText>(appKey + "/views");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

await foreach (var _ in brain.On<TextChanged>(registry, Refresh, brain.Stopping)) { await Refresh(); }
async Task Refresh() { await app.Invoke(new(Guid.NewGuid(), "refresh", "")); }
