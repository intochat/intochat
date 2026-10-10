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

await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    try
    {
        await ComposeAsync();
        // Docked: the shell renders the surface in its side panel, so no workspace window opens.
        if (request.Input != "docked")
        {
            await brain.Get<IWorkspace>(brain.Setting("Brain")!)
                .EnsureOpenAsync(appKey + "/window", "Customer Researcher",
                    WindowReference.For(new UiChildRef(UIVocabulary.SurfaceType, appKey + "/surface")), brain.Stopping);
        }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await brain.Get<IText>(appKey + "/status").Set(error.Message); }
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
    var missingStorage = string.IsNullOrWhiteSpace((await app.Read()).Connections.GetValueOrDefault("tables"));
    await brain.Get<IText>(Name("configuration")).Set(missingStorage
        ? "Database connection required. Bind a Postgres connection to this app's tables slot in app configuration, then reopen and retry."
        : "");
    var children = new List<UiChildRef>();
    var extents = new List<double>();
    if (missingStorage) { children.Add(Ref(UIVocabulary.TextType, "configuration")); extents.Add(88); }
    children.AddRange([Ref(UIVocabulary.TextFieldType, "company"), Ref(UIVocabulary.LayoutType, "toolbar"), Ref(UIVocabulary.TextType, "status"), Ref(UIVocabulary.LayoutType, "research-activity"), Ref(UIVocabulary.WebBrowserType, "browser")]);
    extents.AddRange([60, 40, 64, 0, 0]);
    var main = brain.Get<ILayout>(Name("main"));
    await main.Set(new LayoutDefinition("column",
        children.ToArray(),
        Extents: extents.ToArray()), (await main.Read()).Revision);

    var surface = brain.Get<ISurface>(Name("surface"));
    await surface.Set(new SurfaceDefinition("Customer Researcher", [Ref(UIVocabulary.LayoutType, "main")]), (await surface.Read()).Revision);
}
