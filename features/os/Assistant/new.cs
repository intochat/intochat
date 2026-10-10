using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text;

// The "new" request: start another conversation and clear the status line.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = Assistant.Key(brain);
var app = brain.Get<IApp>(key);
await foreach (var request in brain.On(app, "new", brain.Stopping))
{
    await Start("c-" + request.RequestId.ToString("N"));
}

async Task Start(string id)
{
    var current = brain.Get<IText>(key + "/conversation");
    if ((await current.Read()).Markdown != id) { await current.Set(id); }
    await brain.Get<IText>(key + "/status").Set("");
}
