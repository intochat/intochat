using System.Net;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Button;
using DigitalBrain.Flutter.Button.Signals;
using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Flutter.Tests.Button;

[Collection(FlutterHostCollection.Name)]
public sealed class ButtonHttpFacts(FlutterHostFixture host)
{
    [Fact]
    public async Task ClickPublishesClicked()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        var button = brain.Get<IButton>(UiScope.Key(ws, "go"));
        await using var clicks = await brain.Observe<ButtonClicked>(button, ct);
        using var set = await person.Http.PostAsJsonAsync($"/brains/{ws}/ui/buttons/go/set", new { label = "Open", action = "navigate:docs" }, ct);
        Assert.Equal(HttpStatusCode.Accepted, set.StatusCode);
        using var click = await person.Http.PostAsJsonAsync($"/brains/{ws}/ui/buttons/go/click", new { }, ct);
        Assert.Equal(HttpStatusCode.Accepted, click.StatusCode);
        Assert.Equal("navigate:docs", (await clicks.NextAsync(ct: ct)).Action);
    }
}
