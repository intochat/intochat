#:project /brain/src/Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj
#:project /brain/src/Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Contracts/DigitalBrain.Modules.Flutter.Contracts.csproj
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.TextField;

// The host verifies a scratch copy of the app and names it in the "App" setting.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
// Preferences live under the brain scope, shared by every Settings copy in that brain.
var brainScope = appKey[..appKey.LastIndexOf("/apps/", StringComparison.Ordinal)];
string Prefs() => brainScope + "/settings";
var failures = 0;

// The preferences are the brain's shared, durable state. Scenarios that write them must leave
// them exactly as found, or a verification run inside a person's brain overwrites their values.
async Task<(string Name, string Theme)> SavePrefs() =>
    ((await brain.Get<ITextField>(Prefs() + "/display-name").Read()).Value,
     (await brain.Get<ITextField>(Prefs() + "/theme").Read()).Value);

async Task RestorePrefs((string Name, string Theme) saved)
{
    await brain.Get<ITextField>(Prefs() + "/display-name").SetValue(saved.Name);
    await brain.Get<ITextField>(Prefs() + "/theme").SetValue(saved.Theme);
}

await Scenario("Opening settings composes the surface and answers the defaults", async () =>
{
    var payload = JsonDocument.Parse(await Ask("open", "")).RootElement;
    if (payload.GetProperty("title").GetString() != "Settings")
    { throw new InvalidOperationException("Settings did not answer the generic window contract."); }
    var surfaceName = payload.GetProperty("surface").GetString()!;
    var surface = await brain.Get<ISurface>(surfaceName).Read();
    if (surface.Definition.Title != "Settings") { throw new InvalidOperationException($"The surface title was \"{surface.Definition.Title}\"."); }
    if (surface.Definition.Children.Length == 0) { throw new InvalidOperationException("The surface has no content."); }
    // Preferences are brain-level and may carry earlier values; the contract is their bounded
    // shape, not emptiness.
    var preferences = payload.GetProperty("preferences");
    if (preferences.GetProperty("displayName").GetString()!.Length > 120) { throw new InvalidOperationException("The display name is not bounded."); }
    if (preferences.GetProperty("theme").GetString() is not ("system" or "light" or "dark")) { throw new InvalidOperationException("The theme is not a known value."); }
});

await Scenario("Typed values are the preferences", async () =>
{
    var saved = await SavePrefs();
    try
    {
        await Ask("open", "");
        await brain.Get<ITextField>(Prefs() + "/display-name").SetValue("Ada");
        await brain.Get<ITextField>(Prefs() + "/theme").SetValue("dark");

        var preferences = JsonDocument.Parse(await Ask("read", "")).RootElement.GetProperty("preferences");

        if (preferences.GetProperty("displayName").GetString() != "Ada") { throw new InvalidOperationException("The display name was not answered."); }
        if (preferences.GetProperty("theme").GetString() != "dark") { throw new InvalidOperationException("The theme was not answered."); }
    }
    finally { await RestorePrefs(saved); }
});

await Scenario("Preferences survive reopening", async () =>
{
    var saved = await SavePrefs();
    try
    {
        await Ask("open", "");
        await brain.Get<ITextField>(Prefs() + "/display-name").SetValue("Ada");
        await brain.Get<ITextField>(Prefs() + "/theme").SetValue("light");

        var preferences = JsonDocument.Parse(await Ask("open", "")).RootElement.GetProperty("preferences");

        if (preferences.GetProperty("displayName").GetString() != "Ada" || preferences.GetProperty("theme").GetString() != "light")
        { throw new InvalidOperationException("Reopening lost the preferences."); }
        if ((await brain.Get<ITextField>(Prefs() + "/display-name").Read()).Value != "Ada")
        { throw new InvalidOperationException("The field no longer holds the display name."); }
    }
    finally { await RestorePrefs(saved); }
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

async Task<string> Ask(string operation, string input)
{
    var invocation = await app.Invoke(new InvokeApp(Guid.NewGuid(), operation, input));
    var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
    while (invocation.Status == InvocationStatus.Pending)
    {
        if (DateTimeOffset.UtcNow > deadline) { throw new TimeoutException("The app did not answer within 5 minutes."); }
        await Task.Delay(200);
        invocation = await app.ReadInvocation(invocation.Id);
    }
    if (invocation.Status == InvocationStatus.Failed) { throw new InvalidOperationException(invocation.Error ?? "The app failed."); }
    return invocation.Output ?? "";
}
