using DigitalBrain.AI.Routing;
using DigitalBrain.Apps;

// The assistant tool path: "send" by request. The composer path lives in submissions.cs.
// Jev first: the request is asked of the router and this reaction ends; routed.cs answers it.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Assistant.Key(brain));
await foreach (var request in brain.On(app, "send", brain.Stopping))
{
    if (request.Input.Length is < 1 or > 32000) { continue; }
    await app.Progress("listening", "Heard you");
    if (request.Input.Length > Assistant.MaxRoutedChars)
    {
        await Assistant.AskModel(brain, await app.Read(), request.Input, "", request.RequestId);
        continue;
    }
    try { await brain.Get<IRouter>(Assistant.Scope(brain)).Ask(new RouteRequest(request.RequestId, request.Input), brain.Stopping); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        await Assistant.Status(brain, "Jev unavailable; asking the model. " + error.Message[..Math.Min(error.Message.Length, 200)]);
        await app.Progress("attention", "Jev unavailable; asking the model");
        await Assistant.AskModel(brain, await app.Read(), request.Input, "", request.RequestId);
    }
}
