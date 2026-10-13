using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.WebBrowser;
using DigitalBrain.Flutter.Workspace;

// The researcher's window: edge neurons composed by plain grain calls, safe to re-run. The browser
// part declares its own driver, so no behavior manages the browser session.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var status = brain.Get<IText>(appKey + "/status");
// The last failure an open showed in the status, so a later successful open can take it back.
var failure = brain.Get<IText>(appKey + "/open-failure");

await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    try
    {
        await ComposeAsync();
        // Docked: the shell renders the surface in its side panel, so no workspace window opens.
        if (request.Input != "docked")
        {
            var workspace = brain.Get<IWorkspace>(brain.Setting("Brain")!);
            await workspace.Show(appKey, request.RequestId.ToString(), (await workspace.Read()).Revision);
        }
        if ((await failure.Read()).Markdown is { Length: > 0 } shown)
        {
            if ((await status.Read()).Markdown == shown) { await status.Set(""); }
            await failure.Set("");
        }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        var message = error.Message[..Math.Min(error.Message.Length, 500)];
        await failure.Set(message);
        await status.Set(message);
    }
}

async Task ComposeAsync()
{
    string Name(string part) => appKey + "/" + part;
    UiChildRef Ref(string kind, string part) => new(kind, Name(part));

    await brain.Get<ITextField>(Name("company")).Configure("Company name, location or website", "text", Name("research"));
    await brain.Get<IButton>(Name("research")).Set("Research", "research");
    await brain.Get<IButton>(Name("stop")).Set("Stop", "stop");
    var toolbar = brain.Get<ILayout>(Name("toolbar"));
    await toolbar.Set(new LayoutDefinition("row",
        [Ref(UIVocabulary.ButtonType, "research"), Ref(UIVocabulary.ButtonType, "stop")]), (await toolbar.Read()).Revision);

    await brain.Get<IWebBrowser>(Name("browser")).Configure(Name("browser"));
    // The activity list scrolls on its own; the browser stays mounted below it while research runs.
    UiChildRef[] children = [Ref(UIVocabulary.TextFieldType, "company"), Ref(UIVocabulary.LayoutType, "toolbar"), Ref(UIVocabulary.TextType, "status"), Ref(UIVocabulary.LayoutType, "research-activity"), Ref(UIVocabulary.WebBrowserType, "browser")];
    var main = brain.Get<ILayout>(Name("main"));
    await main.Set(new LayoutDefinition("column", children, Extents: [60, 40, 64, 0, 0]), (await main.Read()).Revision);

    var surface = brain.Get<ISurface>(Name("surface"));
    await surface.Set(new SurfaceDefinition("Customer Researcher", [Ref(UIVocabulary.LayoutType, "main")]), (await surface.Read()).Revision);
}
