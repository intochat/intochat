using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Time;
using DigitalBrain.UI;

namespace Units.HnDigest;

[Signal("hn.digested")]
public sealed record HnDigested(int TopScore) : Signal;

[State("digest.hn.top")]
public sealed record DigestState(string TopTitle = "", int TopScore = 0, string TopUrl = "", int TotalDigests = 0)
{
    public DigestState() : this("", 0, "", 0) { }
}

public static class HnDigestUnit
{
    [Synapse(typeof(Tick), "digest.hn.top", Every = "every 1h")]
    public static async Task<Reaction<DigestState>> Poll(Tick tick, DigestState state, [Slot] IHttpRead hn, CancellationToken ct)
    {
        var topIds = await hn.GetJson("https://hacker-news.firebaseio.com/v0/topstories.json", ct);
        var firstId = topIds.ValueKind == JsonValueKind.Array && topIds.GetArrayLength() > 0 ? topIds[0].GetInt64() : 0L;

        if (firstId > 0)
        {
            var story = await hn.GetJson($"https://hacker-news.firebaseio.com/v0/item/{firstId}.json", ct);
            var title = story.TryGetProperty("title", out var t) ? t.GetString() ?? "" : state.TopTitle;
            var score = story.TryGetProperty("score", out var s) ? s.GetInt32() : 0;
            var url = story.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

            var next = state with
            {
                TopTitle = title,
                TopScore = score,
                TopUrl = url,
                TotalDigests = state.TotalDigests + 1
            };
            return Reaction<DigestState>.Of(next, new HnDigested(score));
        }

        return Reaction<DigestState>.Of(state);
    }

    [Synapse("digest.hn.top")]
    public static View Show(DigestState state) => new("Hacker News Scheduled Digest", new Column(
    [
        new Heading("Hacker News #1", Subtitle: state.TopTitle.Length == 0 ? "No story digested yet." : state.TopTitle),
        new Row(
        [
            new Badge($"{state.TopScore} points", Tone: state.TopScore > 300 ? "accent" : "neutral"),
            new Badge($"Digest #{state.TotalDigests}", Tone: "ok"),
        ]) { Gap = 8 },
        new Text(state.TopUrl) { Mono = true },
    ]) { Gap = 8 });
}
