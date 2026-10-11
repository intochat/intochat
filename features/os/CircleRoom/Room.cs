using DigitalBrain.Circles;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;

// Shared by every synapse of the circle room: the keys of the circle brain's own neurons and the transcript,
// always rebuilt from the chat.
public static class Room
{
    public static string Key(DigitalBrainConnection brain) => brain.Setting("App")!;
    public static string Circle(DigitalBrainConnection brain) => brain.Setting("Brain") ?? Key(brain)[..Key(brain).IndexOf("/apps/", StringComparison.Ordinal)];

    public static ICircleChat Chat(DigitalBrainConnection brain) => brain.Get<ICircleChat>(ICircleChat.Key(Circle(brain)));

    public static Task Status(DigitalBrainConnection brain, string message) => brain.Get<IText>(Key(brain) + "/status").Set(message);

    public static async Task Project(DigitalBrainConnection brain)
    {
        var lines = await Chat(brain).Read(0);
        await brain.Get<IText>(Key(brain) + "/transcript").Set(lines.Length == 0 ? "No messages yet."
            : string.Join("\n", lines.OrderBy(line => line.Sequence).Select(line => line.Speaker + ": " + line.Text)));
    }
}
