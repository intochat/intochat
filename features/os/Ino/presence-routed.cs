using DigitalBrain.AI.Routing;
using DigitalBrain.AI.Routing.Signals;
using DigitalBrain.Client;

// A route decided: Ino is thinking.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var router = brain.Get<IRouter>(Ino.Scope(brain));
await foreach (var routed in brain.On<Routed>(router, brain.Stopping)) { await Ino.Fire(brain, "think"); }
