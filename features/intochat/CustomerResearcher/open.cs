using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.WebBrowser;

// The researcher's main content: edge neurons composed by plain grain calls, safe to re-run. The browser
// part declares its own driver, so no behavior manages the browser session.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var status = brain.Get<IText>(appKey + "/status");
// The last failure an open showed in the status, so a later successful open can take it back.
var failure = brain.Get<IText>(appKey + "/open-failure");

await ComposeAsync();

await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    try
    {
        await ComposeAsync();
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

    await brain.Get<IApp>(appKey).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", appKey + "/main"), Views: [new("full", new("layout", appKey + "/main"))]));
}
