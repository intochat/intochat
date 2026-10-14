using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;

// The researcher's main content: generic edge neurons composed by C#, safe to re-run.
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

    await brain.Get<ICard>(Name("intro")).Set("Customer Researcher", "Research official websites. Keep only verified details and their sources.");
    await brain.Get<ITextField>(Name("company")).Configure("Company name or website", "text", Name("research"));
    await brain.Get<IButton>(Name("research")).Set("Research company", "research");
    await brain.Get<IButton>(Name("research")).SetVisual(new(Svg: "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><circle cx='10' cy='10' r='6' fill='none' stroke='currentColor' stroke-width='1.8'/><path d='m15 15 5 5' stroke='currentColor' stroke-width='1.8' stroke-linecap='round'/></svg>", Tone: "accent"));
    await brain.Get<IButton>(Name("stop")).Set("Stop research", "stop");
    await brain.Get<IButton>(Name("stop")).SetVisual(new(Svg: "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><rect x='6' y='6' width='12' height='12' rx='3' fill='none' stroke='currentColor' stroke-width='1.8'/></svg>"));
    var toolbar = brain.Get<ILayout>(Name("toolbar"));
    await toolbar.Set(new("row", [Ref("button", "research"), Ref("button", "stop")], Gap: 8), (await toolbar.Read()).Revision);
    var composer = brain.Get<ILayout>(Name("composer"));
    await composer.Set(new("column", [Ref("textfield", "company"), Ref("layout", "toolbar")],
        Gap: 8, Extents: [56, 44], Surface: "floating", Padding: 12), (await composer.Read()).Revision);
    var evidence = brain.Get<IText>(Name("live-evidence"));
    if (string.IsNullOrWhiteSpace((await evidence.Read()).Markdown))
    { await evidence.Set("## Sources and evidence\n\nStart research to follow the pages observed by your private research browser. Verified findings will appear here with source links and quotes."); }
    var evidenceLayout = brain.Get<ILayout>(Name("evidence"));
    await evidenceLayout.Set(new("list", [Ref("text", "live-evidence")]), (await evidenceLayout.Read()).Revision);
    var main = brain.Get<ILayout>(Name("main"));
    await main.Set(new("column", [Ref("card", "intro"), Ref("layout", "composer"), Ref("text", "status"),
        Ref("layout", "research-activity"), Ref("layout", "evidence")],
        Gap: 12, Extents: [128, 132, 32, 140, 0]), (await main.Read()).Revision);

    await brain.Get<IApp>(appKey).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", appKey + "/main"), Views: [new("full", new("layout", appKey + "/main"))]));
}
