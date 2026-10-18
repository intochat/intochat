using IntoChat.OS.Circles.Signals;
using IntoChat.OS.Client;

// The one reaction to a line spoken in this circle: the transcript is rebuilt from the chat.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await brain.React<CircleSpoke>(Room.Chat(brain), async _ =>
{
    try { await Room.Project(brain); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
}, brain.Stopping);
