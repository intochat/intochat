using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Circles;
using DigitalBrain.Client;

// "invite": the owner invites a brain by id; the invited brain hears first.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "invite") { continue; }
    try
    {
        var (name, brainId) = await CircleView.Split(brain, request.Input);
        await CircleView.Circle(brain, name).Invite(brainId);
        await CircleView.Status(brain, "Invited " + brainId + " to " + name + ".");
        await CircleView.Project(brain);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Status(brain, error.Message); }
}
