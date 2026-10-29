using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Views;

// The view's own navigation: `detail:<section>/<i>`, `back`, `remove-ask` and `remove-cancel` change the view state,
// then the view is shown again. Install, Update, Open and Remove are button neurons with their own activations; any
// other action (copy: among them) is not ours.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Apps.Key(brain) + "/apps-view"), brain.Stopping))
{
    if (Apps.Command(brain, clicked.Action) is not { } command) { continue; }
    try
    {
        await command();
        await Apps.Show(brain);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine("Apps could not change its view: " + error.GetType().Name); }
}
