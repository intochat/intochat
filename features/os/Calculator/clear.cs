using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text;

// The dock's Clear button: the request empties the display, the same neuron every key press reads.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var display = brain.Get<IText>(appKey + "/display");
await foreach (var request in brain.On(app, "clear", brain.Stopping))
{
    await display.Set("");
}