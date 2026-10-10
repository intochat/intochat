using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Circles;
using DigitalBrain.Client;

// "say": speak in a circle. watch.cs rebuilds the transcript when CircleSpoke arrives.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "say") { continue; }
    try
    {
        var (name, text) = await CircleView.Split(brain, request.Input);
        if (name.Length == 0) { throw new InvalidOperationException("Select or name a circle first."); }
        await CircleView.Chat(brain, name).Say(text);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Status(brain, error.Message); }
}
