using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Modules.Flutter.TextField.Signals;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.TextField;

public sealed class TextFieldFacts(FlutterHost host)
{
    [Fact]
    public async Task ConditionalProjectionPreservesAnEditArrivingAfterTheRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
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
        var brain = host.Brain;
        var field = brain.Get<ITextField>("preference");
        var before = await field.Read();
        var results = await Task.WhenAll(field.TrySetValue("first", before.Version), field.TrySetValue("second", before.Version));
        Assert.Single(results, applied => applied);
        Assert.Equal(before.Version + 1, (await field.Read()).Version);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("secret")]
    public async Task SecretKindsKeepOnlyThatAValueIsSetAndJournalNoValue(string kind)
    {
        var field = host.Brain.Get<ITextField>($"secret-{kind}");
        await field.SetValue("typed before configure");
        await field.Configure("Password", kind);
        await field.SetValue("hunter2-set");
        await field.Input("hunter2-input");
        var state = await field.Read();
        Assert.DoesNotContain("hunter2", state.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("typed before", state.Value, StringComparison.Ordinal);
        Assert.NotEmpty(state.Value);
        var journal = await field.Read(0, 100);
        var changes = journal.Signals.OfType<TextFieldChanged>().ToArray();
        Assert.True(changes.Length >= 3);
        Assert.All(changes, changed => Assert.DoesNotContain("hunter2", changed.Value, StringComparison.Ordinal));
        Assert.All(changes, changed => Assert.DoesNotContain("typed before", changed.Value, StringComparison.Ordinal));
        Assert.All(changes, changed => Assert.Equal("", changed.Value));
        await field.Configure("Name", "text");
        Assert.Equal("", (await field.Read()).Value);
    }

    [Trait("host", "own")]
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
