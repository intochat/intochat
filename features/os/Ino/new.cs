using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
var ledger = await RequestLedger.LoadAsync(brain, "new");
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation is not ("new" or "new-conversation")
        || ledger.Handled(request.RequestId)) { continue; }
    try { await Ino.NewConversation(brain, request.RequestId); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Ino.Status(brain, Ino.Failure(InoCopy.NewChatFailed, error)); }
    await ledger.Record(request.RequestId);
}
