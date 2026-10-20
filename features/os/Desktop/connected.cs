using IntoChat.OS.Client;
using IntoChat.OS.Integrations;

// When a connection changes (the OAuth callback stored it), the sign-in is done: the gallery
// re-renders and the card reads Connected.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var changed in brain.On<ConnectionsChanged>(brain.Get<IConnectionRequests>(Desktop.Scope(brain)), brain.Stopping))
{
    try
    {
        await Desktop.Connecting(brain).Set("");
        await Desktop.Show(brain);
    }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not show the new connection: " + error.Message); }
}
