using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Time;
using DigitalBrain.UI;

namespace Units.WebWatch;

[Signal("watch.polled")]
public sealed record Polled(int Count) : Signal;

[State("watch.web")]
public sealed record Watch(int Unseen = 0, string Latest = "", string LastKey = "")
{
    public Watch() : this(0, "", "") { }
}

public static class WebWatchUnit
{
    [Synapse(typeof(Tick), "watch.web", Every = "every 6m")]
    public static async Task<Reaction<Watch>> Poll(Tick tick, Watch watch, [Slot] IHttpRead http, CancellationToken ct)
    {
        var feed = await http.GetJson("https://example.com/feed.json", ct);
        var items = feed.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array ? list : default;
        var count = 0;
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (Key(item) is { Length: > 0 } key && watch.LastKey.Length > 0 && key == watch.LastKey)
                {
                    break;
                }

                count++;
            }
        }

        var latest = count > 0 && items[0].TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
            ? title.GetString() ?? watch.Latest
            : watch.Latest;
        var lastKey = count > 0 && Key(items[0]) is { Length: > 0 } firstKey
            ? firstKey
            : watch.LastKey;
        return Reaction<Watch>.Of(watch with { Unseen = watch.Unseen + count, Latest = latest, LastKey = lastKey }, new Polled(count));
    }

    private static string Key(JsonElement item) =>
        item.TryGetProperty("id", out var id) && (id.ValueKind == JsonValueKind.String ? id.GetString() : id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null) is { Length: > 0 } k ? k
        : item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && url.GetString() is { Length: > 0 } u ? u
        : item.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String && title.GetString() is { Length: > 0 } t ? t
        : "";

    [Synapse("watch.seen", "watch.web")]
    public static Watch Seen(SignalEnvelope seen, Watch watch) => watch with { Unseen = 0 };

    [Synapse("watch.web")]
    public static View Show(Watch watch) => new("Web watch", new Column(
    [
        new Heading("Web watch", Subtitle: watch.Latest.Length == 0 ? "No items polled yet." : watch.Latest),
        new Badge($"{watch.Unseen} unseen", Tone: watch.Unseen > 10 ? "error" : "accent"),
        new Button("mark-seen", "Mark seen", new Command(".", "watch.seen"))
        {
            Style = Mix.Style()
                .Tone(watch.Unseen > 0 ? UiTone.Accent : UiTone.Secondary)
                .Padding(14, 8)
                .Radius(10)
                .Motion(UiMotion.Snappy)
                .OnPress(static s => s.Scale(0.98)),
        },
    ]) { Gap = 12 });
}
