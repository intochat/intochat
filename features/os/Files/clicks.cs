using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;

// An entry button holds its path: a name with an extension is a file to read, anything else a folder to browse.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);

await Task.WhenAll(Enumerable.Range(0, Machine.MaxEntries).Select(Entry));
return;

async Task Entry(int index)
{
    await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(appKey + "/entry-" + index), brain.Stopping))
    { await app.Request(Path.HasExtension(clicked.Action) ? "read" : "browse", clicked.Action); }
}
