using System.Text.Json;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Toggle;
using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Flutter.Tests.Toggle;

[Collection(FlutterHostCollection.Name)]
public sealed class ToggleHttpFacts(FlutterHostFixture host)
{
    [Fact]
    public async Task GetMatchesSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        await brain.Get<IToggle>(UiScope.Key(ws, "dark")).Set("Dark", true);
        var state = await person.Http.GetFromJsonAsync<ToggleState>($"/brains/{ws}/ui/toggles/dark", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        Assert.True(state!.On);
    }
}
