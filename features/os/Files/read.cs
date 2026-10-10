using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;

// "read" shows a file's text in the content area; a .cs file is shown as C# source.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);

await foreach (var request in brain.On(app, "read", brain.Stopping))
{
    try
    {
        await brain.Get<IText>(appKey + "/location").Set(request.Input);
        await brain.Get<IText>(appKey + "/content").Set(await Machine.Text(brain, request.Input));
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested)
    { await brain.Get<IText>(appKey + "/content").Set("This file could not be read."); }
}
