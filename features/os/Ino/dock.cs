using DigitalBrain.AI.Routing;
using DigitalBrain.AI.Routing.Signals;
using DigitalBrain.Apps;
using DigitalBrain.Client;

// When a route names another app, that app's surface docks beside Ino. The router's pending request holds
// the route Jev decided; routes back into the assistant or into Ino itself stay on the home scene.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var scope = Ino.Scope(brain);
var router = brain.Get<IRouter>(scope);
await foreach (var routed in brain.On<Routed>(router, brain.Stopping))
{
    try
    {
        if (await router.Pending(routed.Id, brain.Stopping) is not { Result.App: { } name }) { continue; }
        var target = IApp.Key(scope, name);
        if (target == Ino.Key(brain) || target == Ino.AssistantKey(brain)) { continue; }
        await Ino.Stage(brain).Go(Ino.HomeKey(brain), target + "/surface");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine($"Route {routed.Id} could not dock its app: {error.Message}"); }
}
