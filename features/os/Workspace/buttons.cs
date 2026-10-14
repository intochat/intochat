using DigitalBrain.Client;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Flutter.Workspace.Signals;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var sources = Sources.Named<IWorkspace, WorkspaceChanged>(Workspace.State(brain), async (_, _) => await Workspace.Inputs(brain));
await foreach (var clicked in brain.On<ButtonClicked>(sources, brain.Stopping))
{
    await Workspace.Navigate(brain, clicked.Action, clicked.RequestId.ToString());
}
