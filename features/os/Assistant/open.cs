using DigitalBrain.Apps;

// Compose the declared root at startup; workspace navigation owns visibility and focus.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Assistant.Key(brain));
var ledger = await RequestLedger.LoadAsync(brain, "open");
await Assistant.ComposeAsync(brain);
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    if (ledger.Handled(request.RequestId)) { continue; }
    try
    {
        await Assistant.ComposeAsync(brain);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Assistant.Status(brain, "Could not open the assistant: " + Assistant.Clip(error.Message)); }
    await ledger.Record(request.RequestId);
}
