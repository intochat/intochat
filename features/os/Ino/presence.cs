using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
await foreach (var activity in brain.On<AppActivity>(brain.Get<IApp>(Ino.AssistantKey(brain)), brain.Stopping))
{
    await brain.Get<IApp>(Ino.Key(brain)).WriteBlackboard(Ino.ProgramRevision(brain), new() { ["mood"] = Ino.MoodOf(activity.State) });
}
