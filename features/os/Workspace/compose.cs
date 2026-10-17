using DigitalBrain.Client;
using DigitalBrain.Flutter.Workspace.Signals;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
await Workspace.Compose(brain);
await foreach (var changed in brain.On<WorkspaceChanged>(Workspace.State(brain), brain.Stopping))
{
    await Workspace.Compose(brain);
}
