using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Button.Signals;
using IntoChat.OS.Flutter.Workspace;
using IntoChat.OS.Flutter.Workspace.Signals;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var sources = Sources.Named<IWorkspace, WorkspaceChanged>(Workspace.State(brain), async (_, _) => await Workspace.Inputs(brain));
await foreach (var clicked in brain.On<ButtonClicked>(sources, brain.Stopping))
{
    await Workspace.Navigate(brain, clicked.Action, clicked.RequestId.ToString());
}
