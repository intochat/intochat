using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Time;
using DigitalBrain.UI;

namespace Units.WebWatch;

[Signal("watch.polled")]
public sealed record Polled(int Count) : Signal;

[State("watch.web")]
public sealed record Watch(int Unseen = 0, string Latest = "")
{
    public Watch() : this(0, "") { }
}

public static class WebWatchUnit
{
    [Synapse(typeof(Tick), "watch.web", Every = "every 6m")]
    public static async Task<Reaction<Watch>> Poll(Tick tick, Watch watch, [Slot] IHttpRead http, CancellationToken ct)
    {
        var feed = await http.GetJson("https://example.com/feed.json", ct);
        var items = feed.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array ? list : default;
        var count = items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
        var latest = count > 0 && items[0].TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
            ? title.GetString() ?? watch.Latest
            : watch.Latest;
        return Reaction<Watch>.Of(watch with { Unseen = watch.Unseen + count, Latest = latest }, new Polled(count));
    }

    [Synapse("watch.seen", "watch.web")]
    public static Watch Seen(SignalEnvelope seen, Watch watch) => watch with { Unseen = 0 };

    [Synapse("watch.web")]
    public static View Show(Watch watch) => new("Web watch", new Column(
    [
        new Heading("Web watch", Subtitle: watch.Latest.Length == 0 ? "No items polled yet." : watch.Latest),
        new Badge($"{watch.Unseen} unseen", Tone: watch.Unseen > 10 ? "error" : "accent"),
        new Button("mark-seen", "Mark seen", new Emit("watch.web", "watch.seen")),
    ]) { Gap = 12 });
}
