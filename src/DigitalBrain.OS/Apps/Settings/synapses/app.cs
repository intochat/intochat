using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.TextField;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
// Preferences are the brain's, not the installation's: keyed by the brain scope they survive
// the app being renamed, reinstalled or forked. Only the derived UI stays app-keyed.
var prefsRoot = brain.Setting("Brain") is { Length: > 0 } brainScope ? brainScope + "/settings" : appKey;
var displayName = brain.Get<ITextField>(prefsRoot + "/display-name");
var theme = brain.Get<ITextField>(prefsRoot + "/theme");

var draftName = brain.Get<ITextField>(appKey + "/preferences/display-name");
var draftTheme = brain.Get<ITextField>(appKey + "/preferences/theme");
using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
var loops = new[] { Operations() };
await Task.WhenAny(loops);
lifetime.Cancel();
await Task.WhenAll(loops);

async Task Operations()
{
    await foreach (var _ in brain.On<AppInvoked>(app, Reconcile, lifetime.Token)) { await Reconcile(); }
}

async Task Reconcile()
{
    foreach (var request in (await app.Pending()).Where(item => item.Operation is "open" or "read"))
    {
        try { await app.Respond(await Answer(request.Id, request.Operation, request.Input)); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested)
        { await app.Respond(new(request.Id, null, "Settings could not load. Reopen Settings to try again.")); }
    }
}

async Task<AppResponse> Answer(Guid id, string operation, string input)
{
    if (operation == "open")
    {
        await Compose();
        // Refresh is owned by the connection projection behavior, not this surface composer.
        var refresh = Guid.NewGuid();
        await app.Invoke(new(refresh, "refresh-connections", ""));
        await app.Await(refresh);
    }
    var name = (await displayName.Read()).Value;
    var chosen = (await theme.Read()).Value;
    // Preferences are bounded the way the shell renders them: a short name, a known theme.
    var preferences = new
    {
        displayName = name.Length > 120 ? name[..120] : name,
        theme = chosen is "dark" or "light" ? chosen : "system",
    };
    return new(id, JsonSerializer.Serialize(new { id = appKey + "/window", title = "Settings", surface = appKey + "/surface", preferences }), null);
}

// Surface composition is separate from preference persistence and connection discovery.
async Task Compose()
{
    // App-owned edge fields hold editable drafts; shared brain preferences remain authoritative on Save.
    var reconcile = Guid.NewGuid();
    await app.Invoke(new(reconcile, "reconcile-preferences", ""));
    await app.Await(reconcile);
    await draftName.Configure("Display name", "text");
    await draftTheme.Configure("Theme (system, light or dark)", "text");
    await brain.Get<IButton>(appKey + "/save-preferences").Set("Save preferences", "save");
    await brain.Get<IButton>(appKey + "/reload-preferences").Set("Reload preferences", "refresh");
    await brain.Get<IButton>(appKey + "/refresh-connections").Set("Refresh connections", "refresh");
    var toolbar = brain.Get<ILayout>(appKey + "/toolbar");
    await toolbar.Set(new("row", [new("button", appKey + "/save-preferences"), new("button", appKey + "/reload-preferences")]), (await toolbar.Read()).Revision);
    var main = brain.Get<ILayout>(appKey + "/main");
    await main.Set(new("column", [new("textfield", appKey + "/preferences/display-name"), new("textfield", appKey + "/preferences/theme"),
        new("layout", appKey + "/toolbar"), new("text", appKey + "/preferences/status"), new("button", appKey + "/refresh-connections"), new("layout", appKey + "/connections")], Extents: [64, 64, 40, 48, 40, 0]),
        (await main.Read()).Revision);
    var surface = brain.Get<ISurface>(appKey + "/surface");
    await surface.Set(new("Settings", [new("layout", appKey + "/main")]), (await surface.Read()).Revision);
}
