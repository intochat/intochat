using System.Text.Json;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Calendar;
using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Flutter.Tests.Calendar;

[Collection(FlutterHostCollection.Name)]
public sealed class CalendarHttpFacts(FlutterHostFixture host)
{
    [Fact]
    public async Task GetMatchesSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        await brain.Get<ICalendar>(UiScope.Key(ws, "cal")).Set("day", ["2026-09-20"]);
        var state = await person.Http.GetFromJsonAsync<CalendarState>($"/brains/{ws}/ui/calendars/cal", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        Assert.Equal("day", state!.Mode);
    }
}
