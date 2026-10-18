using IntoChat.OS.Apps;
using IntoChat.OS.Circles;
using IntoChat.OS.Client;

// "share": the asking member's app goes into this circle brain; the circle core installs it (R124).
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(Room.Key(brain));
await foreach (var request in brain.On(app, "share", brain.Stopping))
{
    try
    {
        var words = request.Input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) { throw new ArgumentException("Name an app to share."); }
        await brain.Get<ICircle>(ICircle.Key(Room.Circle(brain))).Share(words[0], words[1..]);
        await Room.Status(brain, "Shared " + words[0] + ".");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Room.Status(brain, error.Message); }
}
