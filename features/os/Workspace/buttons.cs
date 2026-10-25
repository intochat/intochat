using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Button;
using IntoChat.OS.Modules.Flutter.Button.Signals;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Modules.Flutter.Workspace.Signals;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var sources = Sources.Named<IWorkspace, WorkspaceChanged, IButton>(Workspace.State(brain), async (_, _) => await Workspace.Inputs(brain));
await foreach (var clicked in brain.On<ButtonClicked>(sources, brain.Stopping))
{
    await Workspace.Navigate(brain, clicked.Action, clicked.RequestId.ToString());
}
