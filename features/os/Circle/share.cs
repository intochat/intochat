using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Client;

// "share": the owner shares one of their apps with a circle.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
await foreach (var request in brain.On(app, "share", brain.Stopping))
{
    try
    {
        var (name, shared) = await CircleView.Split(brain, request.Input);
        if (name.Length == 0 || shared.Length == 0) { throw new ArgumentException("Name a circle and an app."); }
        if (!await CircleView.HasApp(brain, shared)) { throw new InvalidOperationException("You have no app named " + shared + "."); }
        await CircleView.Circle(brain, name).Share(shared, []);
        await CircleView.Status(brain, "Shared " + shared + " with " + name + ".");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Fail(brain, error); }
}
