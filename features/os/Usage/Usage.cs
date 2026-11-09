using DigitalBrain;
using DigitalBrain.UI;

public sealed record UsageState(
    long SpentMicros = 0,
    string Status = "Recorded compute spending over the last 30 days.")
{
    public UsageState() : this(0, "Recorded compute spending over the last 30 days.")
    {
    }
}

[App("usage")]
public sealed class UsageScriptApp : App<UsageState>
{
    [On("open")]
    public Task Open()
    {
        Set(State);
        return Task.CompletedTask;
    }

    [On("refresh")]
    public Task Refresh()
    {
        Set(State with { Status = "Refreshed compute spending." });
        return Task.CompletedTask;
    }

    public View Render() => new(
        "Usage",
        new Column(
        [
            new Surface(
                [
                    new Text(State.Status),
                ],
                Kind: SurfaceKind.Elevated,
                Title: "Usage",
                Subtitle: "Amounts in USD · 1,000,000 micro-credits = $1."),
            new Button("Refresh usage", new ButtonAction("usage", "refresh")),
        ]));
}
