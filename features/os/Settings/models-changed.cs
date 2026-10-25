using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Entries;
using IntoChat.OS.Modules.Flutter.Entries.Signals;

// A key saved or removed on a key control can change which models are available: show the models again
// (Show reconfigures the default-model choices). The signal carries the control's name only, never a value.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = Settings.Key(brain);
var keys = Sources.Of([.. SettingsCopy.KeyProviders.Select(provider => brain.Get<IEntryControl>(appKey + "/keys/" + provider.Id))]);
await foreach (var changed in brain.On<EntryControlChanged>(keys, brain.Stopping))
{
    if (!Settings.IsKeyControl(appKey, changed.Name)) { continue; }
    try { await Settings.Show(brain); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine("Settings could not refresh models: " + error.GetType().Name); }
}
