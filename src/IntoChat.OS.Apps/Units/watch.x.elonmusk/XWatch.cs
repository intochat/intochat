using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Time;
using DigitalBrain.UI;

namespace Units.XWatch;

[Signal("x.polled")]
public sealed record Polled(int Fresh) : Signal;

[State("watch.x.elonmusk")]
public sealed record Watch(int Unseen = 0, string Latest = "", string LastId = "")
{
    public Watch() : this(0, "", "") { }
}

public static class XWatchUnit
{
    [Synapse(typeof(Tick), "watch.x.elonmusk", Every = "every 6m")]
    public static async Task<Reaction<Watch>> Poll(Tick tick, Watch watch, [Slot] IHttpRead x, CancellationToken ct)
    {
        var feed = await x.GetJson("https://api.x.com/2/users/elonmusk/mentions", ct);
        var posts = feed.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d : default;
        var fresh = 0;
        if (posts.ValueKind == JsonValueKind.Array)
        {
            foreach (var post in posts.EnumerateArray())
            {
                if (post.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.String
                    && pid.GetString() is { Length: > 0 } id && watch.LastId.Length > 0 && id == watch.LastId)
                {
                    break;
                }

                fresh++;
            }
        }

        var latest = fresh > 0 && posts[0].TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? watch.Latest
            : watch.Latest;
        var lastId = fresh > 0 && posts[0].TryGetProperty("id", out var firstId) && firstId.ValueKind == JsonValueKind.String
            && firstId.GetString() is { Length: > 0 } fid
            ? fid
            : watch.LastId;
        return Reaction<Watch>.Of(watch with { Unseen = watch.Unseen + fresh, Latest = latest, LastId = lastId }, new Polled(fresh));
    }

    [Synapse("x.seen", "watch.x.elonmusk")]
    public static Watch Seen(SignalEnvelope seen, Watch watch) => watch with { Unseen = 0 };

    [Synapse("watch.x.elonmusk")]
    public static View Show(Watch watch) => new("Musk X Watch", new Column(
    [
        new Heading("Musk X Watch", Subtitle: watch.Latest.Length == 0 ? "No mentions recorded yet." : watch.Latest),
        new Badge($"{watch.Unseen} unseen", Tone: watch.Unseen > 10 ? "red" : watch.Unseen > 0 ? "accent" : "neutral"),
        new Text(watch.Latest),
        new Button("mark-seen", "Mark seen", new Emit("watch.x.elonmusk", "x.seen")),
    ]) { Gap = 8 });
}
