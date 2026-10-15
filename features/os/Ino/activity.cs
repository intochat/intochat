using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Chat;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
var chat = brain.Get<IChat>(Ino.DataKey(brain) + "/transcript");
long delivered = -1;
await foreach (var change in brain.On<ChatRunChanged>(chat, Reconcile, brain.Stopping))
{
    await Reconcile();
}

async Task Reconcile()
{
    var state = await chat.Read();
    if (state.ActivityRevision <= delivered) { return; }
    switch (state.Run?.Status)
    {
        case ChatRunStatus.Running:
            await app.Progress("working", "Working on your message");
            break;
        case ChatRunStatus.Completed:
            await app.Progress("completed", "Answered");
            break;
        case ChatRunStatus.Failed:
            await app.Progress("attention", "The turn failed");
            break;
    }
    delivered = state.ActivityRevision;
}
