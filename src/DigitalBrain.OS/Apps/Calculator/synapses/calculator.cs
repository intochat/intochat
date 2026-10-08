using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Workspace;

// The calculator: a display and a keypad of edge neurons. Opening it composes the surface and shows
// the window; each button click reaches this behavior and grows or evaluates the expression. State
// is the display neuron, so a restarted behavior resumes from what the person sees.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var display = brain.Get<IText>(appKey + "/display");
var gate = new SemaphoreSlim(1, 1);
var keys = new[] { "7", "8", "9", "/", "4", "5", "6", "*", "1", "2", "3", "-", "C", "0", "=", "+" };
var labels = new[] { "7", "8", "9", "\u00f7", "4", "5", "6", "\u00d7", "1", "2", "3", "\u2212", "C", "0", "=", "+" };

await Task.WhenAll(Open(), Clicks());
return;

string Name(string part) => appKey + "/" + part;

async Task Open()
{
    await foreach (var invoked in app.Invocations(brain, ["open"], brain.Stopping))
    {
        if (invoked.Operation != "open") { continue; }
        try
        {
            await Compose();
            var surface = new UiChildRef(UIVocabulary.SurfaceType, Name("surface"));
            if (invoked.Input != "docked")
            {
                var scope = appKey[..appKey.LastIndexOf("/apps/", StringComparison.Ordinal)];
                await brain.Get<IWorkspace>(scope)
                    .EnsureOpenAsync("calculator", "Calculator", WindowReference.For(surface), CancellationToken.None);
            }
            await app.Respond(new AppResponse(invoked.InvocationId, "opened", null));
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        { await app.Respond(new AppResponse(invoked.InvocationId, null, error.Message)); }
    }
}

async Task Clicks() => await Task.WhenAll(Enumerable.Range(0, keys.Length).Select(Key));

async Task Key(int index)
{
    var button = brain.Get<IButton>(Name("key-" + index));
    await foreach (var clicked in brain.On<ButtonClicked>(button, brain.Stopping)) { await Press(clicked.Action); }
}

async Task Press(string action)
{
    await gate.WaitAsync(brain.Stopping);
    try
    {
        var expression = (await display.Read()).Markdown;
        var next = action switch
        {
            "C" => "",
            "=" => Evaluate(expression),
            _ => expression + action,
        };
        await display.Set(next);
    }
    finally { gate.Release(); }
}

static string Evaluate(string expression)
{
    if (expression.Length == 0) { return ""; }
    try { return Convert.ToString(new DataTable().Compute(expression, null)) ?? "Error"; }
    catch { return "Error"; }
}

async Task Compose()
{
    for (var index = 0; index < keys.Length; index++)
    { await brain.Get<IButton>(Name("key-" + index)).Set(labels[index], keys[index]); }
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
