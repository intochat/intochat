using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// The view's own navigation: `tab:<id>`, `archived-more`, `delete` and `delete-cancel` change the view state, then
// the view is shown again. Unarchive is archived.cs's; any other action (copy: among them) is not ours.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var commands = Settings.Commands(brain);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Settings.Key(brain) + "/view"), brain.Stopping))
{
    if (clicked.Action.StartsWith("unarchive:", StringComparison.Ordinal) || commands.Command(clicked.Action) is not { } command) { continue; }
    try
    {
        await command();
        await Settings.Show(brain);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine("Settings could not change its view: " + error.GetType().Name); }
}
