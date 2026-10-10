using System.Text.Json;
using DigitalBrain.AI.Routing;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Microsoft.Windows;
using DigitalBrain.Microsoft.CSharp.Files;
using ITimer = DigitalBrain.Time.Timers.ITimer;

// The Jev console: type a request, Jev routes it over the capabilities below and carries it out. When
// the route builds an app, this opens the built app. The prompt, the answer and the buttons are edge
// neurons; the router's decision and memory live in its own neuron.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
// What Jev may act on here: building apps, writing C#, the PC's console and files, timers and notes.
string[] Capabilities = [typeof(IAppStudio).FullName!, typeof(IApp).FullName!, typeof(ICSharpFile).FullName!,
    typeof(IWindows).FullName!, typeof(IConsole).FullName!, typeof(IFileSystem).FullName!, typeof(ITimer).FullName!, typeof(IText).FullName!];
var scope = brain.Setting("Brain")!;

await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    try
    {
        switch (request.Operation)
        {
            case "open":
                await Compose();
                if (request.Input != "docked")
                {
                    await brain.Get<IWorkspace>(scope).EnsureOpenAsync(appKey + "/window", "Jev",
                        WindowReference.For(new UiChildRef(UIVocabulary.SurfaceType, Name("surface"))), brain.Stopping);
                }
                break;
            case "ask" when request.Input.Length is >= 1 and <= 32000:
                await Ask(request.Input);
                break;
            case "ask":
                await brain.Get<IText>(Name("result")).Set("A request has 1-32000 characters.");
                break;
        }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await brain.Get<IText>(Name("result")).Set(error.Message[..Math.Min(error.Message.Length, 2000)]); }
}

string Name(string part) => appKey + "/" + part;

async Task<string> Ask(string request)
{
    var result = brain.Get<IText>(Name("result"));
    await result.Set("Working\u2026");
    var run = await brain.Get<IRouter>(scope).Achieve(request, Capabilities, brain.Stopping);
    var summary = run.Steps.Length == 0
        ? "I could not find a way to do that."
        : string.Join("\n", run.Steps.Select(step => "\u2022 " + step.Method + (step.Error is null ? "" : " (failed: " + step.Error + ")")));
    var answer = run.Answer is { Length: > 0 } and not null ? summary + "\n\n" + run.Answer : summary;
    await result.Set(answer);
    await OpenBuiltApps(run);
    return answer;
}

// When a step built an app, open its surface if it has one, so what Jev made is on screen to use.
async Task OpenBuiltApps(GoalRun run)
{
    foreach (var step in run.Steps.Where(step => step.Contract == typeof(IAppStudio).FullName && step.Result is not null))
    {
        using var document = JsonDocument.Parse(step.Result);
        var name = document.RootElement.TryGetProperty("name", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) { continue; }
        var built = brain.Get<IApp>(scope + "/apps/" + name);
        if ((await built.Read()).Manifest.Operations.Any(operation => operation.Name == "open"))
        { await built.Request("open", ""); }
    }
}

async Task Compose()
{
    await brain.Get<ITextField>(Name("prompt")).Configure("Ask Jev to do something\u2026", "multiline", Name("send"));
    await brain.Get<IButton>(Name("send")).Set("Send", "ask");
    var input = brain.Get<ILayout>(Name("input"));
    await input.Set(new LayoutDefinition("row",
        [new UiChildRef(UIVocabulary.TextFieldType, Name("prompt")), new UiChildRef(UIVocabulary.ButtonType, Name("send"))],
        Extents: [0, 90]), (await input.Read()).Revision);
    var main = brain.Get<ILayout>(Name("main"));
    await main.Set(new LayoutDefinition("column",
        [new UiChildRef(UIVocabulary.TextType, Name("result")), new UiChildRef(UIVocabulary.LayoutType, Name("input"))],
        Extents: [0, 96]), (await main.Read()).Revision);
    var surface = brain.Get<ISurface>(Name("surface"));
    await surface.Set(new SurfaceDefinition("Jev", [new UiChildRef(UIVocabulary.LayoutType, Name("main"))]), (await surface.Read()).Revision);
}
