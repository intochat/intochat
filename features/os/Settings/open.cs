using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.TextField;

// Compose the declared root at startup; workspace navigation owns visibility and focus.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
// Preferences are the brain's, not the installation's: keyed by the brain scope they survive
// the app being renamed, reinstalled or forked. Only the derived UI stays app-keyed.
var prefsRoot = brain.Setting("Brain") is { Length: > 0 } brainScope ? brainScope + "/settings" : appKey;
var displayName = brain.Get<ITextField>(prefsRoot + "/display-name");
var theme = brain.Get<ITextField>(prefsRoot + "/theme");
await Compose();

await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation is not ("open" or "read")) { continue; }
    try
    {
        if (request.Operation == "open")
        {
            await Compose();
            // Refresh is owned by the connection projection behavior, not this surface composer.
            await app.Request("refresh-connections", "");
        }
        // Preferences are neuron state: whoever asked reads {appKey}/preferences.
        var name = (await displayName.Read()).Value;
        var chosen = (await theme.Read()).Value;
        // Bounded the way the shell renders them: a short name, a known theme.
        await brain.Get<IText>(appKey + "/preferences").Set(JsonSerializer.Serialize(new
        {
            displayName = name.Length > 120 ? name[..120] : name,
            theme = chosen is "dark" or "light" ? chosen : "system",
        }));
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested)
    { await brain.Get<IText>(appKey + "/status").Set("Settings could not load. Reopen Settings to try again."); }
}

// Surface composition is separate from preference persistence and connection discovery.
async Task Compose()
{
    await brain.Get<IButton>(appKey + "/refresh-connections").Set("Refresh connections", "refresh");
    var main = brain.Get<ILayout>(appKey + "/main");
    await main.Set(new("column", [new("button", appKey + "/refresh-connections"),
        new("layout", appKey + "/connections")], Extents: [40, 0]), (await main.Read()).Revision);
    await brain.Get<IApp>(appKey).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
            new(new Sphere("icon.svg", []), Main: new("layout", appKey + "/main"), Views: [new("full", new("layout", appKey + "/main"))]));
}
