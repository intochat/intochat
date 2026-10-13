using DigitalBrain.Flutter.Layout;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter.Text;

// One reaction: every request counts the words in its input and writes the answer neuron.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
var main = brain.Get<ILayout>(key + "/main");
await main.Set(new("column", [new("text", key + "/answer")]), (await main.Read()).Revision);
await brain.Get<IApp>(key).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
    new(new Sphere("icon.svg", []), Main: new("layout", key + "/main")));

await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    var words = request.Input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    await brain.Get<IText>(key + "/answer").Set(words == 1 ? "1 word" : $"{words} words");
}
