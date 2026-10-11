using DigitalBrain.Flutter.Text.Signals;

// Any change to the queue's facts (requests, cancellations, outcomes, the active marker) rebuilds the
// derived queue neuron and the research activity list from them.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var facts = Sources.Of(ResearchState.Requests(brain, key), ResearchState.Cancellations(brain, key),
    ResearchState.Outcomes(brain, key), ResearchState.Active(brain, key));
await brain.React<TextChanged>(facts, _ => Activity.Project(brain, key), brain.Stopping);
