using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;

// The queue neuron changed: rebuild the research activity projection.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
await brain.React<TextChanged>(brain.Get<IText>(key + "/queue"), _ => Activity.Project(brain, key), brain.Stopping);
