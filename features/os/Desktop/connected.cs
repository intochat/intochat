using IntoChat.OS.Client;
using IntoChat.OS.Integrations;

// When a connection changes (the OAuth callback stored it), the pending sign-in ends only if its own
// connector is now connected (the signal names no connector); the gallery re-renders either way.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var requests = brain.Get<IConnectionRequests>(Desktop.Scope(brain));
await foreach (var changed in brain.On<ConnectionsChanged>(requests, brain.Stopping))
{
    try
    {
        var pending = (await Desktop.Connecting(brain).Read()).Markdown;
        if (pending.Length > 0 && (await requests.ListOffers()).Any(offer => offer.Id == pending && offer.Connected)) { await Desktop.ClearSignIn(brain); }
        await Desktop.Show(brain);
    }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not show the new connection: " + error.Message); }
}
