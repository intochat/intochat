using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Client;

// "create": a circle exists once its owner's copy holds state; a zero compute share is the first write.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
await foreach (var request in brain.On(app, "create", brain.Stopping))
{
    var name = request.Input.Trim();
    try
    {
        if (!CircleView.IsName(name)) { throw new ArgumentException("A circle name is 1 to 40 of a-z, 0-9 and -."); }
        if ((await CircleView.Names(brain)).Contains(name)) { throw new InvalidOperationException("You already have a circle named " + name + "."); }
        // The selection is written first: the circle write below triggers the rebuild that marks it.
        await CircleView.Select(brain, name);
        await CircleView.Circle(brain, name).SetComputeShare(0);
        await CircleView.Status(brain, "Created " + name + ".");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Status(brain, error.Message); }
}
