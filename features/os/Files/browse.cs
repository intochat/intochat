using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Collection;
using DigitalBrain.Flutter.Text;

// "browse" lists a folder (or the allowed roots for an empty path) in the collection, with a parent entry inside folders.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var listing = brain.Get<ICollectionView>(appKey + "/listing");

await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "browse") { continue; }
    try
    {
        var all = await Machine.Entries(brain, request.Input);
        var shown = all.Take(Machine.MaxEntries).ToArray();
        var kinds = await Task.WhenAll(shown.Select(async path => await Machine.IsFolder(brain, path) ? "folder" : "file"));
        var items = shown.Select((path, index) => new CollectionItem(path, path[(path.LastIndexOfAny(['\\', '/']) + 1)..] is { Length: > 0 } name ? name : path, kinds[index])).ToList();
        if (request.Input.Length > 0) { items.Insert(0, new("up:" + Machine.Parent(request.Input), "..", "folder")); }
        var connected = await Machine.Connection(brain) is not null;
        await brain.Get<IText>(appKey + "/location").Set(request.Input.Length == 0 ? "Allowed folders" : request.Input);
        await brain.Get<IText>(appKey + "/content").Set(all.Length > shown.Length
            ? "Showing " + shown.Length + " of " + all.Length
            : !connected ? "No Windows PC is connected. Connect one and allow a folder." : all.Length == 0 ? "This folder is empty." : "");
        await listing.Set(new CollectionDefinition([.. items], FileMode: false, SelectionEnabled: false), (await listing.Read()).Revision);
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested)
    { await brain.Get<IText>(appKey + "/content").Set("This folder could not be listed."); }
}
