using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Circles;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;

// "say": speak in a circle. watch.cs rebuilds the transcript when the index reports the line.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
var last = brain.Get<IText>(CircleView.Key(brain) + "/said");
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "say") { continue; }
    try
    {
        // Delivery is at-least-once: a request already spoken is not spoken twice.
        if ((await last.Read()).Markdown == request.RequestId.ToString()) { continue; }
        var spoken = await CircleView.Split(brain, request.Input);
        if (spoken.Name.Length == 0) { throw new InvalidOperationException("Select or name a circle first."); }
        if (spoken.Rest.Length == 0) { throw new ArgumentException("There is nothing to say."); }
        await CircleView.Chat(brain, spoken.Name).Say(spoken.Rest);
        await last.Set(request.RequestId.ToString());
        await CircleView.Status(brain, "");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Status(brain, error.Message); }
}
