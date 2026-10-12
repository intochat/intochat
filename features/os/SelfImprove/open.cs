using DigitalBrain.Apps;

// Compose the declared root at startup; workspace navigation owns visibility and focus.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(SelfImprove.Key(brain));
await SelfImprove.ComposeAsync(brain);
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    await SelfImprove.ComposeAsync(brain);
    await SelfImprove.RenderListAsync(brain);
}
