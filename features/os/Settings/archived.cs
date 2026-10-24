using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// `unarchive:<token>` restores the chat the row with that per-render token showed and toasts "Chat restored." Only the unedited shipped
// Settings writes (SettingsCommands checks Settings.MayWrite). The token resolves through {app}/archived-page, so a
// redelivery restores the same chat again, which is harmless.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var commands = Settings.Commands(brain);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Settings.Key(brain) + "/view"), brain.Stopping))
{
    if (!clicked.Action.StartsWith("unarchive:", StringComparison.Ordinal)
        || commands.Command(clicked.Action) is not { } command) { continue; }
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
