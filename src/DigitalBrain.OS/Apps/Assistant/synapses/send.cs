using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter.Form;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Integrations;
using DigitalBrain.Machine;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Postgres;

// The assistant tool path: "send" by request. The composer path lives in submissions.cs.
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
    var choice = snapshot.Settings.GetValueOrDefault("Model", "");
    var model = choice.Length > 0 ? await brain.Get<IModelCatalog>("default").Select(choice) : null;
    var conversation = brain.Get<IAgent>(Assistant.Key(brain) + "/conversations/" + await Assistant.ConversationId(brain));
    await conversation.Start(AgentTurn.Text(request.Input, instructions: snapshot.Files.GetValueOrDefault("prompts/system.md", ""), contracts: contracts)
        with { Model = model, RunId = request.RequestId.ToString("N") });
}
