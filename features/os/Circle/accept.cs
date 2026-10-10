using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Circles;
using DigitalBrain.Client;

// "accept": join the circle this brain was invited to.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "accept") { continue; }
    try
    {
        var name = await CircleView.Resolve(brain, request.Input.Trim()) ?? throw new InvalidOperationException("You have no invitation named " + request.Input.Trim() + ".");
        await CircleView.Circle(brain, name).Accept();
        await CircleView.Select(brain, name);
        await CircleView.Status(brain, "Joined " + CircleView.Plain(name) + ".");
        await CircleView.Project(brain);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Status(brain, error.Message); }
}
