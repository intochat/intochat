using IntoChat.OS.Modules.Apps.AppRuns;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// An app schedule's cadence as [Every] takes it: shorthands and durations tick on a period, a five-field cron on matching minutes.
public sealed class CadenceFacts
{
    [Theory]
    [InlineData("@hourly", 60)]
    [InlineData("@daily", 1440)]
    [InlineData("15m", 15)]
    [InlineData("2h", 120)]
    [InlineData("30s", 1)]
    public void AShorthandOrDurationTicksOnAPeriod(string expression, int minutes)
    {
        var cadence = Cadence.Parse(expression);
        Assert.NotNull(cadence);
        Assert.Equal(TimeSpan.FromMinutes(minutes), cadence!.Period);
        Assert.Null(cadence.Cron);
    }

    [Theory]
    [InlineData("*/15 9-17 * * 1-5", "2026-11-17T09:30:00Z", true)]
    [InlineData("*/15 9-17 * * 1-5", "2026-11-17T09:31:00Z", false)]
    [InlineData("*/15 9-17 * * 1-5", "2026-11-15T09:30:00Z", false)]
    [InlineData("0 8,20 * * *", "2026-11-17T20:00:00Z", true)]
    public void ACronIsDueOnMatchingMinutes(string expression, string at, bool due)
    {
        var cadence = Cadence.Parse(expression);
        Assert.NotNull(cadence);
        Assert.Equal(Cadence.Shortest, cadence!.Period);
        Assert.Equal(due, cadence.IsDue(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("0m")]
    [InlineData("61 * * * *")]
    [InlineData("* * *")]
    public void AnUnreadableCadenceIsRefused(string expression) => Assert.Null(Cadence.Parse(expression));
}
