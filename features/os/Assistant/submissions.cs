using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Models;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Integrations;
using DigitalBrain.Microsoft.Windows;
using DigitalBrain.Flutter.Chat;
using DigitalBrain.Flutter.Form;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Postgres;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var composer = brain.Get<IComposer>(appKey + "/composer");
string[] contracts = [typeof(IConnectionRequests).FullName!, typeof(IPostgresServer).FullName!, typeof(IApp).FullName!,
    typeof(IWorkspace).FullName!, typeof(IForm).FullName!, typeof(ICSharpFile).FullName!,
    typeof(IWindows).FullName!, typeof(IConsole).FullName!, typeof(IFileSystem).FullName!];

await brain.React<ComposerSubmitted>(composer, _ => Reconcile(), brain.Stopping);

async Task Reconcile()
{
    foreach (var request in (await composer.Read()).Submissions.Where(item => item.Status == "pending"))
    {
        try
        {
            await app.Progress("listening", "Heard you");
            var agent = brain.Get<IAgent>(request.Context);
            var state = await agent.Read();
            var runId = request.Id.ToString("N");
            var retained = state.Runs.FirstOrDefault(run => run.RunId == runId);
            if (retained is not null && retained.Status != AgentRunStatus.Completed)
            {
                await composer.Settle(request.Id, retained.Error ?? "The earlier attempt was interrupted. Review your draft and send again.");
                continue;
            }
            if (retained is null && state.ActiveRun?.RunId != runId)
            {
                if (state.ActiveRun is not null)
                {
                    await composer.Settle(request.Id, "A response is already running. Your draft is saved; send it when the response finishes.");
                    continue;
                }
                var snapshot = await app.Read();
                var choice = snapshot.Settings.GetValueOrDefault("Model", "");
                var model = choice.Length > 0 ? await brain.Get<IModelCatalog>("default").Select(choice) : null;
                await agent.Start(AgentTurn.Text(request.Text, instructions: snapshot.Files.GetValueOrDefault("prompts/system.md", ""), contracts: contracts)
                    with { RunId = runId, Model = model });
            }
            await composer.Settle(request.Id);
        }
        catch (Exception error)
        {
            // Admission may have committed before its response failed. Keep the same run ID for retry.
            await composer.Defer(request.Id, "Could not confirm delivery. Your draft is saved; Send retries the same message. " + error.Message);
        }
    }
}
