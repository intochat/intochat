using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Client;

// "accept": join the circle this brain was invited to.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
await foreach (var request in brain.On(app, "accept", brain.Stopping))
{
    try
    {
        var name = await CircleView.Resolve(brain, request.Input.Trim()) ?? throw new InvalidOperationException("You have no invitation named " + request.Input.Trim() + ".");
        // The selection is written first: Accept triggers the rebuild that marks it.
        await CircleView.Select(brain, name);
        await CircleView.Circle(brain, name).Accept();
        await CircleView.Status(brain, "Joined " + CircleView.Plain(name) + ".");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Fail(brain, error); }
}
