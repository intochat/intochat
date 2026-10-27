using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Views;

// The header's model menu: "model:auto" or "model:<id>" stores the selected chat's choice. A choice that is
// not available now changes nothing and says so. The index change marks the view dirty (index.cs); view.cs republishes.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var view = brain.Get<IView>(Ino.Key(brain) + "/view");
var ledger = await RequestLedger.LoadAsync(brain, "model");
await foreach (var clicked in brain.On<ViewAction>(view, brain.Stopping))
{
    if (!clicked.Action.StartsWith("model:", StringComparison.Ordinal)
        || clicked.RequestId == Guid.Empty || ledger.Handled(clicked.RequestId)) { continue; }
    try
    {
        var selection = await Ino.EnsureSelection(brain);
        if (InoChats.TryIdOf(selection.Agent, out var chatId))
        {
            await InoModels.Choose(Ino.Models(brain), InoChats.Index(brain), brain.Get<IText>(Ino.DataKey(brain) + "/status"), chatId, clicked.Action);
        }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { await Ino.Status(brain, Ino.Failure(InoCopy.ModelChangeFailed, error)); }
    await ledger.Record(clicked.RequestId);
}
