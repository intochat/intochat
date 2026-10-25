using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;

// Connection discovery: the refresh request shows the view again, which reads the brain's connections and the
// host's connector offers into ConnectionRow cards (Settings.Connections) and arms each Connect button. The
// registry's own signals (connection-refresh.cs, connection-requested.cs) post that request.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Settings.Key(brain));
await foreach (var _ in brain.On(app, "refresh-connections", brain.Stopping))
{
    try { await Settings.Show(brain); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine("Settings could not refresh connections: " + error.GetType().Name); }
}
