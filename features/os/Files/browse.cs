using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;

// "browse" lists a folder (or the allowed roots for an empty path) as one button per entry.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);

await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "browse") { continue; }
    try
    {
        var entries = (await Machine.Entries(brain, request.Input)).Take(Machine.MaxEntries).ToArray();
        await brain.Get<IText>(appKey + "/location").Set(request.Input.Length == 0 ? "Allowed folders" : request.Input);
        await brain.Get<IText>(appKey + "/content").Set(entries.Length == 0 ? "Nothing to show. Connect a Windows PC and allow a folder." : "");
        for (var index = 0; index < entries.Length; index++)
        { await brain.Get<IButton>(appKey + "/entry-" + index).Set(entries[index], entries[index]); }
        var list = brain.Get<ILayout>(appKey + "/entries");
        await list.Set(new("list", [.. entries.Select((_, index) => new UiChildRef(UIVocabulary.ButtonType, appKey + "/entry-" + index))]),
            (await list.Read()).Revision);
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested)
    { await brain.Get<IText>(appKey + "/content").Set("This folder could not be listed."); }
}
