using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Views;

// The first-run card's view actions. "setup-local" picks the first available local model for the selected chat
// (the index change marks the view dirty); "setup-retry" republishes, which re-reads the model catalog.
// "Add an API key" and "Install Ollama" are armed buttons whose activations the shell handles directly.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var ledger = await RequestLedger.LoadAsync(brain, "setup");
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Ino.Key(brain) + "/view"), brain.Stopping))
{
    if (clicked.Action is not (InoModels.SetupLocal or InoModels.SetupRetry)
        || clicked.RequestId == Guid.Empty || ledger.Handled(clicked.RequestId)) { continue; }
    try
    {
        if (clicked.Action == InoModels.SetupLocal
            && InoChats.TryIdOf((await Ino.EnsureSelection(brain)).Agent, out var chatId)
            && InoModels.FirstLocal(await Ino.Models(brain).List()) is { } local)
        {
            await InoModels.Choose(Ino.Models(brain), InoChats.Index(brain), brain.Get<IText>(Ino.DataKey(brain) + "/status"), chatId, "model:" + local);
        }
        await Ino.Dirty(brain);
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { await Ino.Status(brain, "Couldn't set up the model. Try again."); }
    await ledger.Record(clicked.RequestId);
}
