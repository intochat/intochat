using DigitalBrain.Apps;
using DigitalBrain.Flutter.Button;

// The dock's Clear button presses the keypad's "C" key, so every change to the expression goes through
// the one keypad synapse in click order and the display empties whatever it held. Before "open" has
// composed the keypad there is no key to press, so the request clears the keypad neuron itself.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var clearKey = brain.Get<IButton>(appKey + "/key-" + Array.IndexOf(Keypad.Keys, "C"));
await foreach (var request in brain.On(app, "clear", brain.Stopping))
{
    if ((await clearKey.Read()).Action == "C") { await clearKey.Click(); }
    else { await Keypad.Press(brain, "C", request.RequestId); }
}
