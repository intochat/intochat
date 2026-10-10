using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Circles;
using DigitalBrain.Client;

// "share": the owner shares one of their apps with a circle.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "share") { continue; }
    try
    {
        var (name, shared) = await CircleView.Split(brain, request.Input);
        await CircleView.Circle(brain, name).Share(shared);
        await CircleView.Status(brain, "Shared " + shared + " with " + name + ".");
        await CircleView.Project(brain);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Status(brain, error.Message); }
}
