using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;

// Another conversation was selected: project it.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
await brain.React<TextChanged>(brain.Get<IText>(Assistant.Key(brain) + "/conversation"), _ => Assistant.Project(brain), brain.Stopping);
