using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Integrations;

// A connection authorization was requested: rebuild the connection cards.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var scope = brain.Setting("Brain") ?? appKey[..appKey.IndexOf("/apps/", StringComparison.Ordinal)];
var app = brain.Get<IApp>(appKey);
await brain.React<ConnectionRequested>(brain.Get<IConnectionRequests>(scope), _ => app.Request("refresh-connections", ""), brain.Stopping);
