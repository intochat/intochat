using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Time;
using DigitalBrain.UI;

namespace Units.GithubTriager;

[Signal("issues.triaged")]
public sealed record IssuesTriaged(int OpenCount, int P0Count) : Signal;

[State("triage.github.issues")]
public sealed record IssueState(int OpenCount = 0, int P0Count = 0, string LatestTitle = "", string Repo = "dotnet/orleans")
{
    public IssueState() : this(0, 0, "", "dotnet/orleans") { }
}

public static class GithubIssueTriagerUnit
{
    [Synapse(typeof(Tick), "triage.github.issues", Every = "every 10m")]
    public static async Task<Reaction<IssueState>> Poll(Tick tick, IssueState state, [Slot] IHttpRead github, CancellationToken ct)
    {
        var feed = await github.GetJson($"https://api.github.com/repos/{state.Repo}/issues?state=open", ct);
        var items = feed.ValueKind == JsonValueKind.Array ? feed : default;
        var open = items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
        var p0 = 0;
        var latest = state.LatestTitle;

        if (open > 0)
        {
            latest = items[0].TryGetProperty("title", out var t) ? t.GetString() ?? "" : latest;
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                if (title.Contains("crash", StringComparison.OrdinalIgnoreCase) || title.Contains("p0", StringComparison.OrdinalIgnoreCase))
                {
                    p0++;
                }
            }
        }

        return Reaction<IssueState>.Of(
            state with { OpenCount = open, P0Count = p0, LatestTitle = latest },
            new IssuesTriaged(open, p0));
    }

    [Synapse("triage.reset", "triage.github.issues")]
    public static IssueState Reset(SignalEnvelope env, IssueState state) => state with { P0Count = 0 };

    [Synapse("triage.github.issues")]
    public static View Show(IssueState state) => new("GitHub Issue Triager", new Column(
    [
        new Heading($"Triage: {state.Repo}", Subtitle: state.LatestTitle.Length == 0 ? "No issues loaded." : state.LatestTitle),
        new Row(
        [
            new Badge($"{state.OpenCount} open", Tone: "neutral"),
            new Badge($"{state.P0Count} P0 / crash", Tone: state.P0Count > 0 ? "error" : "accent"),
        ]) { Gap = 8 },
        new Button("reset-alerts", "Acknowledge P0", new Command(".", "triage.reset"))
        {
            Style = Mix.Style()
                .Tone(state.P0Count > 0 ? UiTone.Danger : UiTone.Secondary)
                .Padding(14, 8)
                .Radius(10)
                .Motion(UiMotion.Snappy)
                .OnPress(static s => s.Scale(0.98)),
        },
    ]) { Gap = 8 });
}
