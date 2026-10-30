using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Client;

// The editor's turn failed or was cancelled: the status line says so with the reason. Settled once per run.
await using var brain = await IntoChatOSClient.ConnectAsync(args);

await brain.React<TurnFailed>(PhotoCraft.Editor(brain), async ct =>
{
    _ = ct;
    if (await PhotoCraft.Unsettled(brain) is not { Status: not AgentRunStatus.Completed } run) { return; }
    await PhotoCraft.SetStatus(
        brain,
        PhotoCraft.Clip("The edit did not finish: " + (run.Error ?? run.Status.ToString()), PhotoCraft.MaxStatusChars),
        settledRunId: run.RunId);
}, brain.Stopping);
