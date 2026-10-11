using DigitalBrain.Circles.Signals;
using DigitalBrain.Client;

// The one reaction to a line spoken in this circle: the transcript is rebuilt from the chat.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
await brain.React<CircleSpoke>(Room.Chat(brain), async _ =>
{
    try { await Room.Project(brain); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
}, brain.Stopping);
