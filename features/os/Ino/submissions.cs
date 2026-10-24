using IntoChat.OS.AI.Agents;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Chat;

// The composer path: each pending submission starts a turn in its conversation under the submission id
// and is settled, so a redelivered submission never runs twice. It speaks the same contracts as "send".
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var composer = brain.Get<IComposer>(Ino.DataKey(brain) + "/composer");

await foreach (var submitted in brain.On<ComposerSubmitted>(composer, Reconcile, brain.Stopping))
{
    await Reconcile();
}

async Task Reconcile()
{
    foreach (var request in (await composer.Read()).Submissions.Where(item => item.Status == "pending"))
    {
        try
        {
            await Ino.Advance(brain, "listening", "Heard you");
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
                var model = choice.Length > 0 ? await Ino.Models(brain).Select(choice) : null;
                var contracts = InoTools.Select(Ino.Contracts, await brain.Kernel.Types(brain.Stopping));
                await agent.Start(AgentTurn.Text(request.Text, instructions: snapshot.Files.GetValueOrDefault("prompts/system.md", ""), contracts: contracts)
                    with { RunId = runId, Model = model });
                // The composer asks the model directly; an earlier route's status no longer applies.
                await Ino.Status(brain, "");
                await Ino.Touched(brain, request.Context, request.Text);
            }
            await composer.Settle(request.Id);
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            // Admission may have committed before its response failed. Keep the same run ID for retry.
            await composer.Defer(request.Id, "Could not confirm delivery. Your draft is saved; Send retries the same message. " + error.Message);
        }
    }
}
