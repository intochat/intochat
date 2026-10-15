using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
await foreach (var activity in brain.On<AppActivity>(app, brain.Stopping))
{
    await app.WriteBlackboard(Ino.ProgramRevision(brain),
        new() { ["mood"] = Ino.MoodOf(activity.State) });
}
