using DigitalBrain.Apps;

// Opening the assistant: compose the surface and, unless the shell docks us, open our window.
// A replayed request is skipped, so a window the person closed is never reopened by an old "open".
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Assistant.Key(brain));
var ledger = await RequestLedger.LoadAsync(brain, "open");
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    if (ledger.Handled(request.RequestId)) { continue; }
    try
    {
        await Assistant.ComposeAsync(brain);
        if (request.Input != "docked") { await Assistant.OpenWindowAsync(brain, brain.Stopping); }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Assistant.Status(brain, "Could not open the assistant: " + Assistant.Clip(error.Message)); }
    await ledger.Record(request.RequestId);
}
