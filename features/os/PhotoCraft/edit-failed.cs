using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Client;

// The editor's turn failed or was cancelled: the status line says so with the reason. Settled once per run.
await using var brain = await DigitalBrainClient.ConnectAsync(args);

await brain.React<TurnFailed>(PhotoCraft.Editor(brain), async ct =>
{
    if (await PhotoCraft.Unsettled(brain) is not { Status: not AgentRunStatus.Completed } run) { return; }
    await PhotoCraft.Status(brain).Set(PhotoCraft.Clip("The edit did not finish: " + (run.Error ?? run.Status.ToString()), PhotoCraft.MaxStatusChars));
    await PhotoCraft.Settled(brain).Set(run.RunId);
}, brain.Stopping);
