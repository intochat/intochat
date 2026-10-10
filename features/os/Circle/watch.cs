using DigitalBrain.Circles;
using DigitalBrain.Circles.Signals;
using DigitalBrain.Client;

// The one reaction to any of this brain's circles changing: an invitation, a new member, a line spoken.
// The index publishes CircleTouched for every copy, so the surface is simply rebuilt from the neurons.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var circles = brain.Get<ICircles>(ICircles.Key(CircleView.Scope(brain)));
await brain.React<CircleTouched>(circles, async _ =>
{
    try { await CircleView.Project(brain); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
}, brain.Stopping);
