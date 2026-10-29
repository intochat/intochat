using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Workspace.Signals;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
await Workspace.Compose(brain);
await foreach (var changed in brain.On<WorkspaceChanged>(Workspace.State(brain), brain.Stopping))
{
    await Workspace.Compose(brain);
}
