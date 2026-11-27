using System.Text.Json;
using DigitalBrain;
using DigitalBrain.UI;

namespace Units.GithubTriager;

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
    [Synapse("github.issue-changed", "triage.github.issues")]
    public static Reaction<IssueState> OnIssueChanged(SignalEnvelope env, IssueState state)
    {
        var number = ReadInt(env.Payload, "number");
        var action = ReadString(env.Payload, "action") ?? "updated";
        var subject = ReadString(env.Payload, "subjectKey") ?? "";
        var needsAttention = string.Equals(action, "opened", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "reopened", StringComparison.OrdinalIgnoreCase);
        var nextTriaged = state.TriagedCount + 1;
        var nextP0 = needsAttention ? state.P0Count + 1 : state.P0Count;
        var next = state with
        {
            TriagedCount = nextTriaged,
            P0Count = nextP0,
            LastIssueNumber = number,
            LastAction = action,
            LastSubject = subject,
        };

        return Reaction<IssueState>.Of(next, new IssuesTriaged(number, action, nextTriaged, nextP0));
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

    private static int ReadInt(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object) { return 0; }
        foreach (var prop in payload.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }
        return 0;
    }

    private static string? ReadString(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object) { return null; }
        foreach (var prop in payload.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }
        return null;
    }
}
