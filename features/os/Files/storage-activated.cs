using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Collection;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await foreach (var activated in brain.On<CollectionActivated>(brain.Get<ICollectionView>(appKey + "/storages"), brain.Stopping))
{
    await app.Request("browse", activated.ItemId);
}
