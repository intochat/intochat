using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;

// The Research button publishes the same request the assistant's tools do (Stop: stop-click.cs).
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);

await foreach (var _ in brain.On<ButtonClicked>(brain.Get<IButton>(key + "/research"), brain.Stopping))
{
    var query = (await brain.Get<ITextField>(key + "/company").Read()).Value.Trim();
    if (query.Length is >= 1 and <= 500) { await app.Request("research", query); }
    else { await brain.Get<IText>(key + "/status").Set("Enter a company name (at most 500 characters)."); }
}
