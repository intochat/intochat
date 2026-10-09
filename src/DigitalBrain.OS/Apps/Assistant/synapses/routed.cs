using DigitalBrain.AI.Routing;
using DigitalBrain.AI.Routing.Signals;
using DigitalBrain.Apps;

// Jev's answer to a request send.cs asked: a confident, direct route is carried out by the router;
// anything else anchors the model with the route as a hint. The request lives in the router's state.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Assistant.Key(brain));
var router = brain.Get<IRouter>(Assistant.Scope(brain));
await foreach (var routed in brain.On<Routed>(router, brain.Stopping))
{
    if (await router.Pending(routed.Id, brain.Stopping) is not { } asked) { continue; }
    var snapshot = await app.Read();
    var hint = "";
    try
    {
        if (asked.Result is not { } route)
        {
            await Assistant.Status(brain, "Jev unavailable; asking the model. " + (routed.Error ?? "")[..Math.Min((routed.Error ?? "").Length, 200)]);
        }
        else if (route.Confidence >= Assistant.RouteGate(snapshot) && route.IsDirect)
        {
            var outcome = await router.Dispatch(asked.State, [route.Contract], brain.Stopping);
            await Assistant.Status(brain, outcome.Dispatched
                ? $"Jev: {route.Contract}.{route.Method} ({route.Confidence:0.00}) -> {outcome.Result ?? "done"}"
                : $"Jev chose {route.Contract}.{route.Method} but could not carry it out: {outcome.Error}");
            if (outcome.Dispatched) { continue; }
            hint = "\n\n" + route.Hint();
        }
        else
        {
            await Assistant.Status(brain, $"Jev unsure ({route.Confidence:0.00}); asking the model");
            hint = "\n\n" + route.Hint();
        }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Assistant.Status(brain, "Jev could not carry it out; asking the model. " + error.Message[..Math.Min(error.Message.Length, 200)]); }
    await Assistant.AskModel(brain, snapshot, asked.State, hint, routed.Id);
}
