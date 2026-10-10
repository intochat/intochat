using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Circles;
using DigitalBrain.Circles.Signals;
using DigitalBrain.Client;

// The reaction to a circle changing under us: an invitation, a new member, a line spoken. Whatever
// arrives only says "look again"; the surface is rebuilt from the circle and chat neurons. Operations
// that add circles ask for a look so circles that appear later get watched too; watching one twice is a no-op.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
var watched = new HashSet<string>();
var watchers = new List<Task>();

await Watch();
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation is "open" or "create" or "accept") { await Watch(); }
}
await Task.WhenAll(watchers);

async Task Watch()
{
    foreach (var name in await CircleView.Names(brain))
    {
        if (!watched.Add(name)) { continue; }
        watchers.Add(Rebuild<CircleChanged>(CircleView.Circle(brain, name)));
        watchers.Add(Rebuild<CircleInvited>(CircleView.Circle(brain, name)));
        watchers.Add(Rebuild<CircleSpoke>(CircleView.Chat(brain, name)));
    }
}

async Task Rebuild<T>(INeuron source) where T : Signal
{
    await foreach (var _ in brain.On<T>(source, brain.Stopping))
    {
        try { await CircleView.Project(brain); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }
}
