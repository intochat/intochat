using DigitalBrain.Apps;
using DigitalBrain.Circles;
using DigitalBrain.Client;

// "say": speak in a circle; the chat dedups at-least-once delivery by the request id. watch.cs rebuilds the transcript when the index reports the line.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
await foreach (var request in brain.On(app, "say", brain.Stopping))
{
    try
    {
        var spoken = await CircleView.Split(brain, request.Input);
        if (spoken.Name.Length == 0) { throw new InvalidOperationException("Select or name a circle first."); }
        if (spoken.Rest.Length == 0) { throw new ArgumentException("There is nothing to say."); }
        await CircleView.Chat(brain, spoken.Name).Say(spoken.Rest, request.RequestId.ToString());
        await CircleView.Status(brain, "");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Fail(brain, error); }
}
