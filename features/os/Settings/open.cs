using System.Text.Json;
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

// The surface composer: it opens the window and publishes the current preferences as a neuron.
// Connection discovery belongs to connections.cs, which reacts to its own request.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
// Preferences are the brain's, not the installation's: keyed by the brain scope they survive
// the app being renamed, reinstalled or forked. Only the derived UI stays app-keyed.
var prefsRoot = brain.Setting("Brain") is { Length: > 0 } brainScope ? brainScope + "/settings" : appKey;
var displayName = brain.Get<ITextField>(prefsRoot + "/display-name");
var theme = brain.Get<ITextField>(prefsRoot + "/theme");

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
            if (request.Input != "docked")
            {
                await brain.Get<IWorkspace>(brain.Setting("Brain")!).EnsureOpenAsync(appKey + "/window", "Settings",
                    WindowReference.For(new UiChildRef(UIVocabulary.SurfaceType, appKey + "/surface")), brain.Stopping);
            }
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
    var surface = brain.Get<ISurface>(appKey + "/surface");
    await surface.Set(new("Settings", [new("layout", appKey + "/main")]), (await surface.Read()).Revision);
}
