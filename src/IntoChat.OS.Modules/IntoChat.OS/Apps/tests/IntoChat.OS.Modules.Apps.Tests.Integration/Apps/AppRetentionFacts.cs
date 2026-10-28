using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Signalling;
using IntoChat.OS.Modules.Apps.Signals;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Testing;

using IntoChat.OS.Modules.Apps.Tests.Integration;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps.Tests.Apps;

// A request is retained by its app for synapses that subscribe later: the app keeps the bounded backlog in its state, newest
// last (06-08: the plain app answers it through its host-only IAppMigration.RetainedRequests). A late listener of the app's
// activity starts from the newest AppActivity in the app's journal, the runtime's retained replay.
public sealed class AppRetentionFacts(AppsHost host)
{
    private const string Brain = "app-retention-facts";

    private static async Task<(IApp App, IAppMigration Retains, string Key)> CreatedAppAsync(ITestBrain brain, string name)
    {
        var key = IApp.Key(Brain, name);
        var app = brain.Get<IApp>(key);
        await app.Create(new AppManifest(name, "", [new("open", "")], [], []), "spec");
        return (app, SiloNeurons.Get<IAppMigration>(brain.Services, key), key);
    }

    // What a late listener of the app's activity is replayed first.
    private static async Task<AppActivity?> RetainedActivity(ITestBrain brain, string key, CancellationToken ct)
    {
        var id = SiloNeurons.IdOf(typeof(IApp), key);
        var entry = await SignalLog.Newest(brain.Services.GetRequiredService<IBrainJournalWriter>(), id, SignalLog.Alias(typeof(AppActivity)), ct);
        return entry is null ? null : SignalLog.Decode<AppActivity>(entry.Payload.GetRawText());
    }

    [Fact]
    public async Task ARequestIsRetainedInPublishOrderWithIncreasingSequences()
    {
        var brain = host.Brain;
        var (app, retains, _) = await CreatedAppAsync(brain, "echo");

        var first = await app.Request("open", "one");
        var second = await app.Request("open", "two");

        var retained = await retains.RetainedRequests();
        Assert.Equal(2, retained.Length);
        Assert.Equal([1L, 2L], retained.Select(item => item.Sequence).ToArray());
        Assert.Equal([first, second], retained.Select(item => item.Request.RequestId).ToArray());
    }

    [Fact]
    public async Task The65thRequestEvictsTheOldestRetainedOne()
    {
        var brain = host.Brain;
        var (app, retains, _) = await CreatedAppAsync(brain, "bounded");
        var first = await app.Request("open", "0");
        for (var i = 1; i < 65; i++) { await app.Request("open", i.ToString()); }

        var retained = await retains.RetainedRequests();

        Assert.Equal(64, retained.Length);
        Assert.Equal(65, retained[^1].Sequence);
        Assert.DoesNotContain(first, retained.Select(item => item.Request.RequestId).ToArray());
    }

    [Fact]
    public async Task ALateListenerStartsFromTheLastActivity()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var (app, _, key) = await CreatedAppAsync(brain, "lively");
        await app.Progress("working", "Reading your mail");
        await app.Progress("completed", "Done");

        var activity = await RetainedActivity(brain, key, ct);

        Assert.NotNull(activity);
        Assert.Equal(("lively", "completed", "Done"), (activity.App, activity.State, activity.Detail));
    }

    [Fact]
    public async Task AReactivatedAppWithARetainedWorkingActivitySeedsAttention()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var (app, _, key) = await CreatedAppAsync(brain, "crashed");
        await app.Progress("working", "Reading your mail");

        await brain.DeactivateAsync(app, ct);
        var presentation = (await app.Read()).Presentation;

        var activity = await RetainedActivity(brain, key, ct);
        Assert.NotNull(activity);
        Assert.Equal(("crashed", "attention", "interrupted"), (activity.App, activity.State, activity.Detail));
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
        var brain = host.Brain;
        var (app, _, key) = await CreatedAppAsync(brain, "calm-" + state);
        await app.Progress(state, "steady");

        await brain.DeactivateAsync(app, ct);
        await app.Read();

        var activity = await RetainedActivity(brain, key, ct);
        Assert.NotNull(activity);
        Assert.Equal((state, "steady"), (activity.State, activity.Detail));
    }
}
