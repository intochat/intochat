using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter.Text;

// The dock's Clear button: the request empties the display, the same neuron every key press reads.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var display = brain.Get<IText>(appKey + "/display");
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "clear") { continue; }
    await display.Set("");
}