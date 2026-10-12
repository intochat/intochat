using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.TextField.Signals;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
await foreach (var change in brain.On<TextFieldChanged>(brain.Get<ITextField>(appKey + "/filter"), brain.Stopping))
{
    await brain.Get<IApp>(appKey).Request("browse", (await brain.Get<IText>(appKey + "/navigation").Read()).Markdown);
}
