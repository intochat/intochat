using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Microsoft.Aspire;

// A request is a change set; the index commits it into a worktree and starts it when nothing
// else is live. The answer is the sha in the last neuron (empty when the request was refused); the
// list shows the stage. Delivery is at-least-once, so the handled neuron holds the last request id
// answered and a redelivery of it proposes nothing.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(SelfImprove.Key(brain));
var handled = brain.Get<IText>(SelfImprove.Key(brain) + "/handled");
var last = brain.Get<IText>(SelfImprove.Key(brain) + "/last");
await foreach (var request in brain.On(app, "request", brain.Stopping))
{
    var id = request.RequestId.ToString("N");
    if (request.RequestId != Guid.Empty && (await handled.Read()).Markdown == id) { continue; }
    try
    {
        var sha = await brain.Get<ICandidates>(ICandidates.Key(SelfImprove.Scope(brain))).Propose(SelfImprove.Parse(request.Input));
        // A proposal starts a cluster, so it is marked handled before anything else can fail.
        await handled.Set(id);
        await last.Set(sha);
    }
    catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
    {
        // A refusal is safe to repeat, so it is shown before the request is marked handled.
        await last.Set("");
        await brain.Get<IText>(SelfImprove.Key(brain) + "/report").Set("Request refused: " + error.Message);
        await handled.Set(id);
    }
    await SelfImprove.RenderListAsync(brain);
}
