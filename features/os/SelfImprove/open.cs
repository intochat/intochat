using DigitalBrain.Apps;

// Opening: compose the surface, render the list, open the window unless docked.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(SelfImprove.Key(brain));
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    await SelfImprove.ComposeAsync(brain);
    await SelfImprove.RenderListAsync(brain);
    if (request.Input != "docked") { await SelfImprove.OpenWindowAsync(brain, brain.Stopping); }
}