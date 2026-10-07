#:project /brain/src/DigitalBrain.Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj
#:project /brain/src/DigitalBrain.Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Contracts/DigitalBrain.Modules.Flutter.Contracts.csproj
#:project /brain/src/DigitalBrain.Modules/Microsoft/Playwright/DigitalBrain.Modules.Microsoft.Playwright.Contracts/DigitalBrain.Modules.Microsoft.Playwright.Contracts.csproj
#:project /brain/src/DigitalBrain.Modules/AI/DigitalBrain.Modules.AI.Contracts/DigitalBrain.Modules.AI.Contracts.csproj
using System.Text.Json;
using DigitalBrain.AI.Models.Scripted;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Microsoft.Playwright;

// The deterministic meaning of the spec: scripted browser and scripted model play the connector,
// the app's own operations drive and inspect it, and nothing here touches a live model or site.
// The host verifies a scratch copy of the app and names it in the "App" setting.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var failures = 0;
var round = 0;

const string SearchUrl = "https://www.bing.com/search?q=acme";
const string SiteUrl = "https://acme.example/";
const string SiteText = "Acme Robotics builds warehouse robots. Headquarters: Prague, Czechia. Contact: info@acme.example.";
var searchPage = new BrowserObservation(SearchUrl, "acme - Search", "Results for acme",
    [new BrowserLink("Acme Robotics — official site", SiteUrl)]);
var companyPage = new BrowserObservation(SiteUrl, "Acme Robotics", SiteText, []);
const string Extraction = """
    {"status":"found","companyName":"Acme Robotics","website":"https://acme.example","location":"Prague, Czechia",
     "email":"info@acme.example","phone":null,"industry":null,"summary":"Acme Robotics builds warehouse robots."}
    """;

// Each scenario gets its own scripted model and browser, rebound through settings.
async Task<(IScriptedLLM Model, IScriptedBrowser Browser)> Scripted()
{
    var scope = appKey + "/scenario-" + ++round;
    await app.Configure(new Dictionary<string, string>
    {
        ["Model"] = IScriptedLLM.ModelPrefix + scope + "/model",
        ["Browser"] = "scripted/" + scope + "/browser",
    }, new Dictionary<string, string>());
    return (brain.Get<IScriptedLLM>(scope + "/model"), brain.Get<IScriptedBrowser>(scope + "/browser"));
}

await Scenario("Opening docked answers the surface without a window", async () =>
{
    await Scripted();
    var workspace = brain.Get<IWorkspace>(appKey[..appKey.IndexOf("/apps/", StringComparison.Ordinal)]);
    var before = (await workspace.Read()).Windows.Count(window => window.Id == "customer-researcher" && window.IsOpen);
    var docked = JsonDocument.Parse(await app.Ask("open", "docked")).RootElement;
    var surface = await brain.Get<ISurface>(docked.GetProperty("surface").GetString()!).Read();
    if (surface.Definition.Title != "Customer Researcher" || surface.Definition.Children.Length == 0)
    { throw new InvalidOperationException("The docked surface is not composed."); }
    var after = (await workspace.Read()).Windows.Count(window => window.Id == "customer-researcher" && window.IsOpen);
    if (after != before) { throw new InvalidOperationException("A docked open opened a workspace window."); }
    // The floating open, by contrast, lands in this brain's own workspace.
    await app.Ask("open", "");
    if (!(await workspace.Read()).Windows.Any(window => window.Id == "customer-researcher" && window.IsOpen))
    { throw new InvalidOperationException("A floating open opened no window in the brain's workspace."); }
});

await Scenario("Opening the researcher composes its surface", async () =>
{
    await Scripted();
    var window = JsonDocument.Parse(await app.Ask("open", "")).RootElement;
    if (window.GetProperty("title").GetString() != "Customer Researcher")
    { throw new InvalidOperationException($"The window title was \"{window.GetProperty("title").GetString()}\"."); }
    var surface = await brain.Get<ISurface>(window.GetProperty("surface").GetString()!).Read();
    if (surface.Definition.Title != "Customer Researcher" || surface.Definition.Children.Length == 0)
    { throw new InvalidOperationException("The researcher surface is not composed."); }
});

await Scenario("Stopping an idle researcher is safe", async () =>
{
    await Scripted();
    if (await app.Ask("stop", "") != "Stopped.") { throw new InvalidOperationException("Stop did not answer."); }
});

await Scenario("Research verifies the company against observed pages and saves it", async () =>
{
    var (model, browser) = await Scripted();
    await browser.Script([searchPage, companyPage, companyPage]);
    await model.Script(["""{"linkId":1}""", Extraction]);
    var answer = await app.Ask("research", "Acme Robotics");
    if (answer != "Saved: Acme Robotics") { throw new InvalidOperationException($"Research answered \"{answer}\"."); }
    var row = JsonDocument.Parse(await app.Ask("result", "Acme Robotics")).RootElement;
    if (row.ValueKind != JsonValueKind.Object || row.GetProperty("company_name").GetString() != "Acme Robotics"
        || row.GetProperty("email").GetString() != "info@acme.example"
        || !row.GetProperty("evidence").EnumerateArray().Any(item => item.GetProperty("Field").GetString() == "email"))
    { throw new InvalidOperationException("The saved row does not carry the verified company and its evidence."); }
    var requested = await browser.RequestedUrls();
    if (requested.Length != 2 || !requested[1].StartsWith("https://acme.example", StringComparison.Ordinal))
    { throw new InvalidOperationException("The research did not drive the browser through search and the official site."); }
});

await Scenario("An unverifiable company is refused and saves nothing", async () =>
{
    var (_, browser) = await Scripted();
    await browser.Script(
        [new BrowserObservation(SearchUrl, "ghost - Search", "No usable results", []), new BrowserObservation(SearchUrl, "ghost - Search", "No usable results", [])]);
    var answer = await app.Ask("research", "Ghost Corp");
    if (!answer.StartsWith("Could not select an official company website", StringComparison.Ordinal))
    { throw new InvalidOperationException($"Research answered \"{answer}\"."); }
    if (await app.Ask("result", "Ghost Corp") != "null") { throw new InvalidOperationException("A refused research saved a row."); }
});

await Scenario("Stop during research cancels it or the research completes, never half-saves", async () =>
{
    var (model, browser) = await Scripted();
    await browser.Script([searchPage, companyPage, companyPage]);
    await model.Script(["""{"linkId":1}""", Extraction]);
    // Its own query: the scenarios share one install, and an earlier scenario saved "Acme Robotics".
    var research = await app.Invoke(new InvokeApp(Guid.NewGuid(), "research", "Acme Robotics warehouse"));
    await app.Invoke(new InvokeApp(Guid.NewGuid(), "stop", ""));
    var answer = await app.AwaitResponse(research.Id);
    var saved = await app.Ask("result", "Acme Robotics warehouse");
    // Stop raced a scripted (instant) research: both orders are legal, half-states are not.
    if (answer == "Stopped." ? saved != "null" : answer != "Saved: Acme Robotics" || saved == "null")
    { throw new InvalidOperationException($"Research answered \"{answer}\" but the stored row was {saved}."); }
});

return failures == 0 ? 0 : 1;

async Task Scenario(string name, Func<Task> run)
{
    try
    {
        await run();
        Console.WriteLine($"dbtest:pass {name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"dbtest:fail {name}\t{error.Message.ReplaceLineEndings(" ")}");
    }
}
