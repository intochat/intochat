using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;

// An app request may change the queue: rebuild the research activity projection.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
await brain.React<AppRequested>(brain.Get<IApp>(key), _ => Activity.Project(brain, key), brain.Stopping);
