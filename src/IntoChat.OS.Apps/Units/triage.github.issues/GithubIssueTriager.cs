using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;

namespace Units.GithubTriager;

[Signal("github.issue-changed")]
public sealed record IssueChanged(int Number, string Action, string Version, string SubjectKey) : Signal;

[Signal("issues.triaged")]
public sealed record IssuesTriaged(int IssueNumber, string Action, int TriagedCount, int P0Count) : Signal;

[State("triage.github.issues")]
public sealed record IssueState(
    int TriagedCount = 0,
    int P0Count = 0,
    int LastIssueNumber = 0,
    string LastAction = "",
    string LastSubject = "")
{
    public IssueState() : this(0, 0, 0, "", "") { }
}

public static class GithubIssueTriagerUnit
{
    [Synapse(typeof(IssueChanged), "triage.github.issues")]
    public static Reaction<IssueState> OnIssueChanged(IssueChanged changed, IssueState state)
    {
        var needsAttention = string.Equals(changed.Action, "opened", StringComparison.OrdinalIgnoreCase)
            || string.Equals(changed.Action, "reopened", StringComparison.OrdinalIgnoreCase);
        var nextTriaged = state.TriagedCount + 1;
        var nextP0 = needsAttention ? state.P0Count + 1 : state.P0Count;
        var next = state with
        {
            TriagedCount = nextTriaged,
            P0Count = nextP0,
            LastIssueNumber = changed.Number,
            LastAction = changed.Action,
            LastSubject = changed.SubjectKey,
        };

        return Reaction<IssueState>.Of(next, new IssuesTriaged(changed.Number, changed.Action, nextTriaged, nextP0));
    }

    [Synapse("github.issue-changed", "triage.github.issues")]
    public static Reaction<IssueState> OnIssueEnvelope(SignalEnvelope env, IssueState state)
    {
        var p = env.Payload;
        var number = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("Number", out var n) && n.TryGetInt32(out var num) ? num : 0;
        var action = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("Action", out var a) ? a.GetString() ?? "updated" : "updated";
        var version = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
        var subject = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("SubjectKey", out var s) ? s.GetString() ?? "" : "";
        return OnIssueChanged(new IssueChanged(number, action, version, subject), state);
    }

    [Synapse("triage.reset", "triage.github.issues")]
    public static IssueState Reset(SignalEnvelope env, IssueState state) => state with { P0Count = 0 };

    [Synapse("triage.github.issues")]
    public static View Show(IssueState state) => new("GitHub Issue Triager", new Column(
    [
        new Heading(
            "GitHub Issue Triage",
            Subtitle: state.LastIssueNumber == 0
                ? "Waiting for IssueChanged signals."
                : $"#{state.LastIssueNumber} ({state.LastAction}) · {state.LastSubject}"),
        new Row(
        [
            new Badge($"{state.TriagedCount} triaged", Tone: "neutral"),
            new Badge($"{state.P0Count} attention", Tone: state.P0Count > 0 ? "error" : "accent"),
        ]) { Gap = 8 },
        new Button("reset-alerts", "Acknowledge", new Command(".", "triage.reset"))
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
