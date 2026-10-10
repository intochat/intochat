using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Microsoft.Aspire;

// A request is a change set; the index commits it into a worktree and starts it when nothing
// else is live. The answer is the sha in the last neuron; the list shows the stage.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(SelfImprove.Key(brain));
await foreach (var request in brain.On(app, "request", brain.Stopping))
{
    var last = brain.Get<IText>(SelfImprove.Key(brain) + "/last");
    try
    {
        var sha = await brain.Get<ICandidates>(ICandidates.Key(SelfImprove.Scope(brain))).Propose(SelfImprove.Parse(request.Input));
        await last.Set(sha);
    }
    catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
    {
        await brain.Get<IText>(SelfImprove.Key(brain) + "/report").Set("Request refused: " + error.Message);
    }
    await SelfImprove.RenderListAsync(brain);
}