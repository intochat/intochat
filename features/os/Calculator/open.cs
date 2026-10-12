using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Workspace;

// Compose the declared root at startup; workspace navigation owns visibility and focus.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
await Compose();
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    await Compose();
}

string Name(string part) => appKey + "/" + part;

async Task Compose()
{
    for (var index = 0; index < Keypad.Keys.Length; index++)
    { await brain.Get<IButton>(Name("key-" + index)).Set(Keypad.Labels[index], Keypad.Keys[index]); }
    var children = new List<UiChildRef> { new(UIVocabulary.TextType, Name("display")) };
    var extents = new List<double> { 96 };
    for (var row = 0; row < 4; row++)
    {
        var grid = brain.Get<ILayout>(Name("row-" + row));
        await grid.Set(new LayoutDefinition("row",
            [.. Enumerable.Range(0, 4).Select(column => new UiChildRef(UIVocabulary.ButtonType, Name("key-" + (row * 4 + column))))]),
            (await grid.Read()).Revision);
        children.Add(new UiChildRef(UIVocabulary.LayoutType, Name("row-" + row)));
        extents.Add(80);
    }
    var main = brain.Get<ILayout>(Name("main"));
    await main.Set(new LayoutDefinition("column", [.. children], Extents: [.. extents]), (await main.Read()).Revision);
    var surface = brain.Get<ISurface>(Name("surface"));
    await surface.Set(new SurfaceDefinition("Calculator", [new UiChildRef(UIVocabulary.LayoutType, Name("main"))]), (await surface.Read()).Revision);
}
