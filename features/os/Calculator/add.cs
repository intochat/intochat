using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter.Text;

// "add" sums "a+b" into the sum neuron.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "add") { continue; }
    var parts = request.Input.Split('+');
    await brain.Get<IText>(appKey + "/sum").Set((decimal.Parse(parts[0]) + decimal.Parse(parts[1])).ToString(System.Globalization.CultureInfo.InvariantCulture));
}
