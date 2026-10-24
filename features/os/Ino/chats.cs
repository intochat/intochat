using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.TextField;
using IntoChat.OS.Flutter.Views;

// The sidebar's buttons: open a chat, rename it in a modal, archive it with Undo. Each click is handled once
// and only changes state; view.cs republishes.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var data = Ino.DataKey(brain);
var commands = new InoChatCommands(InoChats.Index(brain), brain.Get<IChat>(data + "/transcript"), brain.Get<ITextField>(data + "/rename"),
    InoViewState.Text(brain), brain.Get<IText>(data + "/status"), data, operation => Ino.NewConversation(brain, operation), () => DateTimeOffset.UtcNow);
var ledger = await RequestLedger.LoadAsync(brain, "chats");
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Ino.Key(brain) + "/view"), brain.Stopping))
{
    if (clicked.RequestId == Guid.Empty || ledger.Handled(clicked.RequestId)
        || commands.Command(clicked.Action, clicked.RequestId) is not { } command) { continue; }
    try { await command(); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { await Ino.Status(brain, Ino.Failure(InoCopy.ChatUpdateFailed, error)); }
    await ledger.Record(clicked.RequestId);
}
