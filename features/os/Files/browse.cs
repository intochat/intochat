using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Collection;
using IntoChat.OS.Flutter.Text;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await foreach (var request in brain.On(app, "browse", brain.Stopping))
{
    try { await Machine.Browse(brain, request.Input); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested)
    {
        var listing = brain.Get<ICollectionView>(appKey + "/listing");
        var previous = await listing.Read();
        await listing.Set(new([], Error: "This storage or folder is unavailable. Choose Storages to try another connection.",
            Breadcrumbs: [new(Machine.Storages, "Storages"), new("unavailable", "Unavailable")]), previous.Revision);
        await brain.Get<IText>(appKey + "/navigation").Set(Machine.Storages);
        await brain.Get<IText>(appKey + "/content").Set("");
    }
}
