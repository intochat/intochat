using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Calendar;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Calendar;

public sealed class CalendarFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SetWritesModeAndSelection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        await brain.Get<ICalendar>("cal").Set("day", ["2026-09-20"]);
        var state = await brain.Get<ICalendar>("cal").Read();
        Assert.Equal("day", state.Mode);
        Assert.Equal(["2026-09-20"], state.Selected);
    }
}
