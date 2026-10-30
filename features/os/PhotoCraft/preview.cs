using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Client;

// The editor finished a turn: its answer becomes the status line and the active document is rendered into
// the preview surface. Rebuilt from the editor's state, and settled once per run.
await using var brain = await IntoChatOSClient.ConnectAsync(args);

await brain.React<TurnCompleted>(PhotoCraft.Editor(brain), async ct =>
{
    if (await PhotoCraft.Unsettled(brain) is not { Status: AgentRunStatus.Completed } run) { return; }
    var session = await PhotoCraft.Connect(brain, ct);
    await PhotoCraft.RenderPreview(brain, session, run.UserText, ct);
    await PhotoCraft.SetStatus(
        brain,
        PhotoCraft.Clip(string.IsNullOrWhiteSpace(run.Reply) ? "Done." : run.Reply, PhotoCraft.MaxStatusChars),
        session.WebUrl ?? "http://localhost:18082",
        settledRunId: run.RunId);
}, brain.Stopping);
