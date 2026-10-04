#:project /brain/src/Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj
#:project /brain/src/Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Contracts/DigitalBrain.Modules.Flutter.Contracts.csproj
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.TextField;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var package = PackageId.Parse(brain.Setting("Package")!);
var revision = brain.Setting("Revision")!;
var failures = 0;
// Mirrors the app's own derivation: preferences live under the brain scope when one exists.
string Prefs(string scope) => brain.Setting("BrainScope") is { Length: > 0 } brainScope ? brainScope + "/settings" : scope + "/app";

// The preferences are the brain's shared, durable state. Scenarios that write them must leave
// them exactly as found, or the next verification run of any Settings revision reads this
// suite's leftovers — and a fork verified inside a person's brain would overwrite their values.
async Task<(string Name, string Theme)> SavePrefs(string scope) =>
    ((await brain.Get<ITextField>(Prefs(scope) + "/display-name").Read()).Value,
     (await brain.Get<ITextField>(Prefs(scope) + "/theme").Read()).Value);

async Task RestorePrefs(string scope, (string Name, string Theme) saved)
{
    await brain.Get<ITextField>(Prefs(scope) + "/display-name").SetValue(saved.Name);
    await brain.Get<ITextField>(Prefs(scope) + "/theme").SetValue(saved.Theme);
}

await Scenario("Opening settings composes the surface and answers bounded preferences", async (app, scope) =>
{
    var payload = JsonDocument.Parse(await Ask(app, "open", "")).RootElement;
    if (payload.GetProperty("id").GetString() != scope + "/app/window" || payload.GetProperty("title").GetString() != "Settings")
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

await Scenario("Typed values are the preferences", async (app, scope) =>
{
    var saved = await SavePrefs(scope);
    try
    {
        await Ask(app, "open", "");
        await brain.Get<ITextField>(Prefs(scope) + "/display-name").SetValue("Ada");
        await brain.Get<ITextField>(Prefs(scope) + "/theme").SetValue("dark");

        var preferences = JsonDocument.Parse(await Ask(app, "read", "")).RootElement.GetProperty("preferences");

        if (preferences.GetProperty("displayName").GetString() != "Ada") { throw new InvalidOperationException("The display name was not answered."); }
        if (preferences.GetProperty("theme").GetString() != "dark") { throw new InvalidOperationException("The theme was not answered."); }
    }
    finally { await RestorePrefs(scope, saved); }
});

await Scenario("Preferences survive reopening", async (app, scope) =>
{
    var saved = await SavePrefs(scope);
    try
    {
        await Ask(app, "open", "");
        await brain.Get<ITextField>(Prefs(scope) + "/display-name").SetValue("Ada");
        await brain.Get<ITextField>(Prefs(scope) + "/theme").SetValue("light");

        var preferences = JsonDocument.Parse(await Ask(app, "open", "")).RootElement.GetProperty("preferences");

        if (preferences.GetProperty("displayName").GetString() != "Ada" || preferences.GetProperty("theme").GetString() != "light")
        { throw new InvalidOperationException("Reopening lost the preferences."); }
        if ((await brain.Get<ITextField>(Prefs(scope) + "/display-name").Read()).Value != "Ada")
        { throw new InvalidOperationException("The field no longer holds the display name."); }
    }
    finally { await RestorePrefs(scope, saved); }
});

await Scenario("Preferences survive a reinstall under a different package identity", async (app, scope) =>
{
    var saved = await SavePrefs(scope);
    try
    {
        await Ask(app, "open", "");
        await brain.Get<ITextField>(Prefs(scope) + "/display-name").SetValue("Grace");
        await app.Uninstall(new UninstallApp(Guid.NewGuid()));

        // A second install at a different app key: brain-keyed preferences carry over; without a
        // brain scope each install is its own world and carrying over is not promised.
        var secondScope = (brain.Setting("BrainScope") is { } owner ? owner + "/" : "") + $"specs/{package}@{revision}/{Guid.NewGuid():N}";
        var second = brain.Get<IApp>(secondScope + "/app");
        await second.Install(new InstallApp(Guid.NewGuid(), new(package, revision), new Dictionary<string, string>()));
        try
        {
            var preferences = JsonDocument.Parse(await Ask(second, "open", "")).RootElement.GetProperty("preferences");
            var expected = brain.Setting("BrainScope") is { Length: > 0 } ? "Grace" : "";
            if (preferences.GetProperty("displayName").GetString() != expected)
            { throw new InvalidOperationException("Preferences did not survive the reinstall."); }
        }
        finally { try { await second.Uninstall(new UninstallApp(Guid.NewGuid())); } catch (Exception) { } }
    }
    finally { await RestorePrefs(scope, saved); }
});

return failures == 0 ? 0 : 1;

async Task Scenario(string name, Func<IApp, string, Task> run)
{
    // Scratch installs live inside the host-supplied brain scope, so the scenario's neurons
    // are brain-owned for target authorization instead of failing closed as unclassified.
    var brainScope = brain.Setting("BrainScope");
    var scope = (brainScope is null ? "" : brainScope + "/") + $"specs/{package}@{revision}/{Guid.NewGuid():N}";
    var app = brain.Get<IApp>(scope + "/app");
    try
    {
        await app.Install(new InstallApp(Guid.NewGuid(), new(package, revision), new Dictionary<string, string>()));
        await run(app, scope);
        Console.WriteLine($"dbtest:pass {name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"dbtest:fail {name}\t{error.Message.ReplaceLineEndings(" ")}");
    }
    finally
    {
        try { await app.Uninstall(new UninstallApp(Guid.NewGuid())); } catch (Exception) { }
    }
}

async Task<string> Ask(IApp app, string operation, string input)
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
