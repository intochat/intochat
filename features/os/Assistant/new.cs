using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text;

// The "new" request: start another conversation and clear the status line. A replayed request is
// skipped, so an old "new" never switches the person back to that conversation.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = Assistant.Key(brain);
var app = brain.Get<IApp>(key);
var ledger = await RequestLedger.LoadAsync(brain, "new");
await foreach (var request in brain.On(app, "new", brain.Stopping))
{
    if (ledger.Handled(request.RequestId)) { continue; }
    try { await Start("c-" + request.RequestId.ToString("N")); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Assistant.Status(brain, "Could not start a new conversation: " + Assistant.Clip(error.Message)); }
    await ledger.Record(request.RequestId);
}

async Task Start(string id)
{
    var current = brain.Get<IText>(key + "/conversation");
    if ((await current.Read()).Markdown != id) { await current.Set(id); }
    await brain.Get<IText>(key + "/status").Set("");
}
