using IntoChat.OS.Apps;
using IntoChat.OS.Client;

// "say": one line in the circle's chat; the chat dedups at-least-once delivery by the request id.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Room.Key(brain));
await foreach (var request in brain.On(app, "say", brain.Stopping))
{
    try
    {
        var text = request.Input.Trim();
        if (text.Length == 0) { throw new ArgumentException("There is nothing to say."); }
        await Room.Chat(brain).Say(text, request.RequestId.ToString());
        await Room.Status(brain, "");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Room.Status(brain, error.Message); }
}
