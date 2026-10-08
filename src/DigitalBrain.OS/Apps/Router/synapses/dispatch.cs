using System.Text.Json;
using DigitalBrain.AI.Routing;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.Workspace;

// The Jev console: type a request, Jev routes it over the contract catalog and carries it out. When
// the route builds an app, this opens the built app. The prompt, the answer and the buttons are edge
// neurons; the router's decision and memory live in its own neuron.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var scope = appKey[..appKey.LastIndexOf("/apps/", StringComparison.Ordinal)];

await Task.WhenAll(Operations(), SendClicks());
return;

string Name(string part) => appKey + "/" + part;

async Task Operations()
{
    await foreach (var invoked in app.Invocations(brain, ["open", "ask"], brain.Stopping))
    {
        try
        {
            switch (invoked.Operation)
            {
                case "open":
                    await Compose();
                    var surface = new UiChildRef(UIVocabulary.SurfaceType, Name("surface"));
                    if (invoked.Input != "docked")
                    {
                        await brain.Get<IWorkspace>(scope)
                            .EnsureOpenAsync("router", "Jev", WindowReference.For(surface), CancellationToken.None);
                    }
                    await app.Respond(new AppResponse(invoked.InvocationId, "opened", null));
                    break;
                case "ask" when invoked.Input.Length is >= 1 and <= 32000:
                    await app.Respond(new AppResponse(invoked.InvocationId, await Ask(invoked.Input), null));
                    break;
                case "ask":
                    await app.Respond(new AppResponse(invoked.InvocationId, null, "A request has 1-32000 characters."));
                    break;
            }
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        { await app.Respond(new AppResponse(invoked.InvocationId, null, error.Message[..Math.Min(error.Message.Length, 2000)])); }
    }
}

async Task<string> Ask(string request)
{
    var result = brain.Get<IText>(Name("result"));
    await result.Set("Working\u2026");
    var run = await brain.Get<IRouter>(scope).Achieve(request, null, brain.Stopping);
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
        { await built.Invoke(new(Guid.NewGuid(), "open", "")); }
    }
}

async Task SendClicks()
{
    await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(Name("send")), brain.Stopping))
    {
        var text = (await brain.Get<ITextField>(Name("prompt")).Read()).Value.Trim();
        if (text.Length is >= 1 and <= 32000)
        { await app.Invoke(new(clicked.RequestId == Guid.Empty ? Guid.NewGuid() : clicked.RequestId, "ask", text)); }
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
