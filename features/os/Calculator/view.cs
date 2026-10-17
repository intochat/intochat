using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Flutter.Views;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Setting("App")!;
var display = brain.Get<IText>(app + "/display");
var publisher = new ViewPublisher(brain.Get<IView>(app + "/view"),
    long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture));
await brain.React<TextChanged>(display, async _ =>
    await publisher.Publish(Keypad.Render((await display.Read()).Markdown)), brain.Stopping);
