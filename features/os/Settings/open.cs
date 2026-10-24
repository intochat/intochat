using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Apps.Signals;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.TextField;

// Startup configures every control the view references, arms its buttons and presents the view on the durable tab.
// `open` takes a tab id (or an old section name) and shows that tab; `read` publishes the stored preferences.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = Settings.Key(brain);
var app = brain.Get<IApp>(appKey);
// Preferences are the brain's, not the installation's: keyed by the brain scope they survive
// the app being renamed, reinstalled or forked.
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
            await SettingsCommands.Open(Settings.ViewState(brain), request.Input);
            await Compose();
        }
        // Preferences are neuron state: whoever asked reads {appKey}/preferences.
        var name = (await displayName.Read()).Value;
        var chosen = (await theme.Read()).Value;
        await brain.Get<IText>(appKey + "/preferences").Set(JsonSerializer.Serialize(new
        {
            displayName = name.Length > 120 ? name[..120] : name,
            theme = chosen is "dark" or "light" ? chosen : "system",
        }));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        Console.Error.WriteLine("Settings could not open: " + error.GetType().Name);
        // Generic copy only, never the error text.
        try
        {
            await Settings.UpdateState(Settings.ViewState(brain), state => state with { Toast = SettingsCopy.OpenFailed });
            await Settings.Show(brain);
        }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }
}

async Task Compose()
{
    // Present even if a control or the first render fails: the next action renders again.
    try
    {
        await Settings.ConfigureControls(brain);
        await Settings.Show(brain);
    }
    finally { await Settings.Present(brain); }
}
