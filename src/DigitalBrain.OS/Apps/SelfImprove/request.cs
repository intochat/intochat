using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Revisions;

// A request is a change set; the index commits it through the launcher and starts it when nothing
// else is live. The answer is the sha in the last neuron; the list shows the stage.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(SelfImprove.Key(brain));
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "request") { continue; }
    var last = brain.Get<IText>(SelfImprove.Key(brain) + "/last");
    try
    {
        var sha = await brain.Get<IRevisions>(SelfImprove.Scope(brain)).Propose(SelfImprove.Parse(request.Input));
        await last.Set(sha);
    }
    catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
    {
        await brain.Get<IText>(SelfImprove.Key(brain) + "/report").Set("Request refused: " + error.Message);
    }
    await SelfImprove.RenderListAsync(brain);
}