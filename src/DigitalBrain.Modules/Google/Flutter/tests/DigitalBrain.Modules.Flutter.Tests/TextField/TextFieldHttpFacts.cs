using System.Net;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Flutter.Tests.TextField;

[Collection(FlutterHostCollection.Name)]
public sealed class TextFieldHttpFacts(FlutterHostFixture host)
{
    [Fact]
    public async Task ScopedValueRouteWritesTheNeuronAndTheUnscopedReadIsGone()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        var field = brain.Get<ITextField>(UiScope.Key(ws, "name"));
        await field.Configure("Name", "secret");

        using var set = await person.Http.PostAsJsonAsync($"/brains/{ws}/ui/textfields/name/value", new { value = "Ada" }, ct);
        Assert.Equal(HttpStatusCode.Accepted, set.StatusCode);
        Assert.Equal("Ada", (await field.Read()).Value);

        using var removed = await person.Http.GetAsync($"/brains/{ws}/ui/textfields/name", ct);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        using var unscoped = await person.Http.GetAsync("/ui/textfields/name", ct);
        Assert.Equal(HttpStatusCode.NotFound, unscoped.StatusCode);
    }
}
