using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Workspace;

// Compose the declared root at startup; workspace navigation owns visibility and focus.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = CircleView.Key(brain);
try { await CircleView.Restore(brain); }
catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
{ await CircleView.Fail(brain, error); }
await foreach (var request in brain.On(brain.Get<IApp>(appKey), "open", brain.Stopping))
{
    try
    {
        await CircleView.Compose(brain);
        if (string.IsNullOrWhiteSpace(request.Input)) await CircleView.Restore(brain);
        else await CircleView.Navigate(brain, PublicationUi.Decode<PublicationUi.Navigation>(request.Input));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Fail(brain, error); }
}
