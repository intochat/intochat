using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// `unarchive:<i>` restores row i of the archived page and toasts "Chat restored." Only the unedited shipped
// Settings writes (SettingsCommands checks Settings.MayWrite). Each click is handled once, so a redelivery
// never restores the row that moved up into its place.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var commands = Settings.Commands(brain);
var handled = new HashSet<Guid>();
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Settings.Key(brain) + "/view"), brain.Stopping))
{
    if (!clicked.Action.StartsWith("unarchive:", StringComparison.Ordinal) || !handled.Add(clicked.RequestId)
        || commands.Command(clicked.Action) is not { } command) { continue; }
    if (handled.Count > 256) { handled.Clear(); handled.Add(clicked.RequestId); }
    try { await command(); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        Console.Error.WriteLine("Settings could not restore a chat: " + error.GetType().Name);
        try { await Settings.UpdateState(Settings.ViewState(brain), state => state with { Toast = SettingsCopy.SaveFailed }); }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
    }
    try { await Settings.Show(brain); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
}
