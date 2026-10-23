using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// More's Settings is a button whose activation (Desktop.SettingsActivation) hands the shell
// {kind: settings}: the shell opens its native Settings screen, not the Settings app.
// A `settings` click from an older render re-arms that button and re-renders the desktop, so the
// shell's next click on More's Settings carries the activation.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (clicked.Action != "settings") { continue; }
    try
    {
        await Desktop.SetSettingsButton(brain);
        await Desktop.Show(brain);
    }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not arm Settings: " + error.Message); }
}
