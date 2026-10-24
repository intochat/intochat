using IntoChat.OS.Client;
using IntoChat.OS.Integrations;

// A connection changed (Connect, or a slot's Add new, finished): show the view again. A slot left on its new
// connection picks that connection once it exists, and Install is re-armed with it.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await brain.React<ConnectionsChanged>(Apps.Connections(brain), _ => Apps.Show(brain), brain.Stopping);
