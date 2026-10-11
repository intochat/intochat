using DigitalBrain.Flutter;
using DigitalBrain.Flutter.TextField;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.TextField;

public sealed class TextFieldFacts
{
    [Fact]
    public async Task ConditionalProjectionPreservesAnEditArrivingAfterTheRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var field = brain.Get<ITextField>("draft");
        await field.SetValue("original");
        var before = await field.Read();
        await field.Input("new user edit");
        var edited = await field.Read();
        Assert.False(await field.TrySetValue("stale projection", before.Version));
        var preserved = await field.Read();
        Assert.Equal("new user edit", preserved.Value);
        Assert.Equal(edited.Version, preserved.Version);
        Assert.True(await field.TrySetValue("current projection", preserved.Version));
        Assert.Equal("current projection", (await field.Read()).Value);
    }

    [Fact]
    public async Task CompetingConditionalWritersCannotBothReplaceTheSameVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var field = brain.Get<ITextField>("preference");
        var before = await field.Read();
        var results = await Task.WhenAll(field.TrySetValue("first", before.Version), field.TrySetValue("second", before.Version));
        Assert.Single(results, applied => applied);
        Assert.Equal(before.Version + 1, (await field.Read()).Version);
    }

    [Fact]
    public async Task ConfigureAndSetValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .StartAsync(ct);
        var field = brain.Get<ITextField>("name");
        await field.Configure("Name", "text");
        await field.SetValue("Ada");
        Assert.Equal("Ada", (await field.Read()).Value);
    }
}
