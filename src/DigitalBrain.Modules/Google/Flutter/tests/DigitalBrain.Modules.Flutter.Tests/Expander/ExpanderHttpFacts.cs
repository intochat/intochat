using System.Net;
using System.Text.Json;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Expander;
using DigitalBrain.Flutter.Expander.Signals;
using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Flutter.Tests.Expander;

[Collection(FlutterHostCollection.Name)]
public sealed class ExpanderHttpFacts(FlutterHostFixture host)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task ToggleHttpFlipsNeuronAndGet()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        var expander = brain.Get<IExpander>(UiScope.Key(ws, "more"));
        await using var changed = await brain.Observe<ExpanderChanged>(expander, ct);

        using var collapsed = await person.Http.PostAsJsonAsync($"/brains/{ws}/ui/expanders/more",
            new { header = "More", expanded = false, children = Array.Empty<object>() }, ct);
        Assert.Equal(HttpStatusCode.Accepted, collapsed.StatusCode);
        Assert.False((await changed.NextAsync(ct: ct)).Expanded);
        Assert.False((await expander.Read()).Expanded);
        Assert.False((await person.Http.GetFromJsonAsync<ExpanderState>($"/brains/{ws}/ui/expanders/more", Json, ct))!.Expanded);

        using var toggle = await person.Http.PostAsync($"/brains/{ws}/ui/expanders/more/toggle", null, ct);
        Assert.Equal(HttpStatusCode.Accepted, toggle.StatusCode);
        Assert.True((await changed.NextAsync(ct: ct)).Expanded);
        Assert.True((await expander.Read()).Expanded);
        Assert.True((await person.Http.GetFromJsonAsync<ExpanderState>($"/brains/{ws}/ui/expanders/more", Json, ct))!.Expanded);
    }
}
