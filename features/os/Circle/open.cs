using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Workspace;

// Compose the declared root at startup; workspace navigation owns visibility and focus.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = CircleView.Key(brain);
await CircleView.Compose(brain);
await CircleView.Project(brain);
await foreach (var request in brain.On(brain.Get<IApp>(appKey), "open", brain.Stopping))
{
    try
    {
        await CircleView.Compose(brain);
        await CircleView.Project(brain);
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Status(brain, "Circles could not load. Reopen Circle to try again."); }
}
