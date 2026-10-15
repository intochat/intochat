using DigitalBrain.Apps.Signals;
using DigitalBrain.Testing;

namespace DigitalBrain.Apps.Tests.Apps;

// A request is retained by its app for synapses that subscribe later: the app answers IRetainsSignals
// with the bounded backlog, newest last, so the brain runtime can seed a late subscription.
public sealed class AppRetentionFacts
{
    private const string Brain = "app-retention-facts";

    private static async Task<(IApp App, IRetainsSignals Retains)> CreatedAppAsync(ITestBrain brain, string name)
    {
        var app = brain.Get<IApp>(IApp.Key(Brain, name));
        await app.Create(new AppManifest(name, "", [new("open", "")], [], []), "spec");
        return (app, app.AsReference<IRetainsSignals>());
    }

    [Fact]
    public async Task ARequestIsRetainedInPublishOrderWithIncreasingSequences()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().StartAsync(ct);
        var (app, retains) = await CreatedAppAsync(brain, "echo");

        var first = await app.Request("open", "one");
        var second = await app.Request("open", "two");

        var retained = await retains.Retained(nameof(AppRequested), 0);
        Assert.Equal(2, retained.Length);
        Assert.Equal([1L, 2L], retained.Select(item => item.Sequence).ToArray());
        var ids = retained.Select(item => Assert.IsType<AppRequested>(item.Signal).RequestId).ToArray();
        Assert.Equal([first, second], ids);
    }

    [Fact]
    public async Task RetainedFiltersToSignalsNewerThanSince()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().StartAsync(ct);
        var (app, retains) = await CreatedAppAsync(brain, "filter");
        await app.Request("open", "one");
        await app.Request("open", "two");

        var retained = await retains.Retained(nameof(AppRequested), 1);

        var item = Assert.Single(retained);
        Assert.Equal(2, item.Sequence);
        Assert.Equal("two", Assert.IsType<AppRequested>(item.Signal).Input);
    }

    [Fact]
    public async Task ARetainingNeuronReturnsNothingForSignalsItDoesNotRetain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().StartAsync(ct);
        var (app, retains) = await CreatedAppAsync(brain, "shy");
        await app.Request("open", "one");

        Assert.Empty(await retains.Retained("SomeOtherSignal", 0));
    }

    [Fact]
    public async Task The65thRequestEvictsTheOldestRetainedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().StartAsync(ct);
        var (app, retains) = await CreatedAppAsync(brain, "bounded");
        var first = await app.Request("open", "0");
        for (var i = 1; i < 65; i++) { await app.Request("open", i.ToString()); }

        var retained = await retains.Retained(nameof(AppRequested), 0);

        Assert.Equal(64, retained.Length);
        Assert.Equal(65, retained[^1].Sequence);
        Assert.DoesNotContain(first, retained.Select(item => Assert.IsType<AppRequested>(item.Signal).RequestId).ToArray());
    }

    [Fact]
    public async Task OnlyTheLastActivityIsRetainedSoALateListenerStartsCurrent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().StartAsync(ct);
        var (app, retains) = await CreatedAppAsync(brain, "lively");
        await app.Progress("working", "Reading your mail");
        await app.Progress("completed", "Done");

        var retained = await retains.Retained(nameof(AppActivity), 0);

        var item = Assert.Single(retained);
        var activity = Assert.IsType<AppActivity>(item.Signal);
        Assert.Equal(("lively", "completed", "Done"), (activity.App, activity.State, activity.Detail));
        Assert.Empty(await retains.Retained(nameof(AppActivity), item.Sequence));
    }

    [Fact]
    public async Task AReactivatedAppWithARetainedWorkingActivitySeedsAttention()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().StartAsync(ct);
        var (app, retains) = await CreatedAppAsync(brain, "crashed");
        await app.Progress("working", "Reading your mail");

        await brain.DeactivateAsync(app, ct);

        var item = Assert.Single(await retains.Retained(nameof(AppActivity), 0));
        var activity = Assert.IsType<AppActivity>(item.Signal);
        Assert.Equal(("crashed", "attention", "interrupted"), (activity.App, activity.State, activity.Detail));
        var presentation = (await app.Read()).Presentation;
        Assert.NotNull(presentation);
        Assert.Equal("false", presentation.Blackboard["running"]);
        Assert.Equal("true", presentation.Blackboard["attention"]);
    }

    [Theory]
    [InlineData("listening")]
    [InlineData("waiting")]
    [InlineData("idle")]
    [InlineData("completed")]
    public async Task AReactivatedAppKeepsARetainedActivityThatIsNotATurnInProgress(string state)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().StartAsync(ct);
        var (app, retains) = await CreatedAppAsync(brain, "calm-" + state);
        await app.Progress(state, "steady");

        await brain.DeactivateAsync(app, ct);

        var activity = Assert.IsType<AppActivity>(Assert.Single(await retains.Retained(nameof(AppActivity), 0)).Signal);
        Assert.Equal((state, "steady"), (activity.State, activity.Detail));
    }
}
