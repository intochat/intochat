using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models;
using DigitalBrain.AI.Routing;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter.Form;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Integrations;
using DigitalBrain.Machine;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Postgres;

// The assistant tool path: "send" by request. The composer path lives in submissions.cs.
// Jev first: a confident, direct route is carried out by the router; anything else anchors the model.
// What the model may act on: the contracts this app composes, checked by the compiler.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Assistant.Key(brain));
string[] contracts = [typeof(IConnectionRequests).FullName!, typeof(IPostgresServer).FullName!, typeof(IApp).FullName!, typeof(IAppIndex).FullName!,
    typeof(IWorkspace).FullName!, typeof(IForm).FullName!, typeof(ICSharpFile).FullName!,
    typeof(IConsole).FullName!, typeof(IFileSystem).FullName!];
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "send" || request.Input.Length is < 1 or > 32000) { continue; }
    var snapshot = await app.Read();
    var router = brain.Get<IRouter>(Assistant.Scope(brain));
    var hint = "";
    try
    {
        var route = await router.Route(request.Input, null, brain.Stopping);
        if (route.Confidence >= Assistant.RouteGate(snapshot) && Assistant.IsDirect(route))
        {
            var outcome = await router.Dispatch(request.Input, [route.Contract], brain.Stopping);
            await Assistant.Status(brain, outcome.Dispatched
                ? $"Jev: {route.Contract}.{route.Method} ({route.Confidence:0.00}) → {outcome.Result ?? "done"}"
                : $"Jev chose {route.Contract}.{route.Method} but could not carry it out: {outcome.Error}");
            if (outcome.Dispatched) { continue; }
        }
        else { await Assistant.Status(brain, $"Jev unsure ({route.Confidence:0.00}); asking the model"); }
        hint = "\n\n" + route.Hint();
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Assistant.Status(brain, "Jev unavailable; asking the model. " + error.Message[..Math.Min(error.Message.Length, 200)]); }

    var choice = snapshot.Settings.GetValueOrDefault("Model", "");
    var model = choice.Length > 0 ? await brain.Get<IModelCatalog>("default").Select(choice) : null;
    var conversation = brain.Get<IAgent>(Assistant.Key(brain) + "/conversations/" + await Assistant.ConversationId(brain));
    await conversation.Start(AgentTurn.Text(request.Input, instructions: snapshot.Files.GetValueOrDefault("prompts/system.md", "") + hint, contracts: contracts)
        with { Model = model, RunId = request.RequestId.ToString("N") });
}
