using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Agents.Signals;
using DigitalBrain.Client;

// The editor finished a turn: its answer becomes the status line and the active document is rendered into
// the preview image. Rebuilt from the editor's state, and settled once per run.
await using var brain = await DigitalBrainClient.ConnectAsync(args);

await brain.React<TurnCompleted>(PhotoCraft.Editor(brain), async ct =>
{
    if (await PhotoCraft.Unsettled(brain) is not { Status: AgentRunStatus.Completed } run) { return; }
    var session = await PhotoCraft.Connect(brain, ct);
    await PhotoCraft.RenderPreview(brain, session, run.UserText, ct);
    await PhotoCraft.Status(brain).Set(PhotoCraft.Clip(string.IsNullOrWhiteSpace(run.Reply) ? "Done." : run.Reply, PhotoCraft.MaxStatusChars));
    await PhotoCraft.Settled(brain).Set(run.RunId);
}, brain.Stopping);
