using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Workspace.Key(brain));
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation == "navigate") { await Workspace.Navigate(brain, request.Input, request.RequestId.ToString()); }
    else if (request.Operation == "open")
    {
        var state = await Workspace.State(brain).Read();
        await Workspace.State(brain).Apply(new(state.OpenViews, state.FocusedViewId, state.Pins), request.RequestId.ToString(), state.Revision);
    }
}
