using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Collection;

// One subscription: an activated entry is the parent (up:), a folder (it lists) or a file (it does not).
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);

await foreach (var activated in brain.On<CollectionActivated>(brain.Get<ICollectionView>(appKey + "/listing"), brain.Stopping))
{
    if (activated.ItemId == Machine.Storages) { await app.Request("browse", Machine.Storages); }
    else { await app.Request(await Machine.IsFolder(brain, activated.ItemId) ? "browse" : "read", activated.ItemId); }
}
