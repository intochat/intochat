using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Chat;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var chat = brain.Get<IChat>(Ino.DataKey(brain) + "/transcript");
long delivered = -1;
await foreach (var change in brain.On<ChatRunChanged>(chat, () => Reconcile(null), brain.Stopping))
{
    await Reconcile(change);
}

// The signal carries the status, so a turn does not read the transcript to discover it.
// Advance publishes the mood with the activity, so no second script watches this write.
async Task Reconcile(ChatRunChanged? change)
{
    long revision;
    ChatRunStatus? status;
    if (change is { Status: { } known })
    {
        if (change.ActivityRevision <= delivered) { return; }
        revision = change.ActivityRevision;
        status = known;
    }
    else
    {
        var state = await chat.Read();
        if (state.ActivityRevision <= delivered) { return; }
        revision = state.ActivityRevision;
        status = state.Run?.Status;
    }
    var step = status switch
    {
        ChatRunStatus.Running => ("working", "Working on your message"),
        ChatRunStatus.Completed => ("completed", "Answered"),
        ChatRunStatus.Failed => ("attention", "The turn failed"),
        _ => ((string State, string Detail)?)null,
    };
    if (step is { } activity)
    {
        await Ino.Advance(brain, activity.State, activity.Detail);
    }
    delivered = revision;
}
