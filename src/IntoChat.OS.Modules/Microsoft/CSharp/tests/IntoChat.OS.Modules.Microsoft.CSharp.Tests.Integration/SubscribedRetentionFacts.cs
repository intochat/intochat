using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// An app's journal is its AppRequested backlog; a synapse that subscribes after its app's requests were published still receives them
// because Subscribed seeds the new subscription's backlog from it. 06-08: every source is plain; the legacy retention seam
// (IRetainsSignals) and the facts about which grains were asked for it are gone.
public sealed class SubscribedRetentionFacts
{
    private const string Brain = "retention-brain";
    private const string Echo = Brain + "/apps/echo";
    private const string Behavior = Echo + "/synapses/a.cs";

    [Trait("host", "own")]
    [Fact]
    public async Task ANewSubscriptionPushesItsRetainedSeedToTheAlreadyConnectedSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartWithAppsAsync(new FakeSessionHost(), ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "echo"));
        await app.Create(new AppManifest("Echo", "", [new("open", "")], [], []), "spec");
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await Edge(brain).Connected(session, RuntimeBrain.Fingerprint(brain));
        var request = await app.Request("open", "before subscription");
        var stream = brain.Services.GetRequiredService<IntoChat.OS.Modules.Microsoft.CSharp.Sessions.SessionStreams>().Open(session);

        var subscription = await Edge(brain).Subscribed(session, Behavior, RuntimeBrain.Plain<IApp>(brain, IApp.Key(Brain, "echo")), RuntimeBrain.Requested);

        Assert.True(stream.Reader.TryRead(out var pushed), "The retained seed must reach the live stream without another signal or reconnect.");
        var batch = Assert.IsType<IntoChat.OS.Client.Runtime.V1.SignalBatchEvent>(pushed.Payload);
        Assert.Equal(subscription, batch.Subscription);
        Assert.Equal(request, SignalJson.RequestId(Assert.Single(batch.Signals)));
        await Edge(brain).Acknowledge(session, subscription, batch.Watermark);
        Assert.Empty(await Edge(brain).PendingBatches(session));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARequestPostedBeforeTheSynapseSubscribesIsSeededIntoPending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartWithAppsAsync(new FakeSessionHost(), ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "echo"));
        await app.Create(new AppManifest("Echo", "", [new("open", "")], [], []), "spec");
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var first = await app.Request("open", "one");
        var second = await app.Request("open", "two");

        var subscription = await Edge(brain).Subscribed(session, Behavior, RuntimeBrain.Plain<IApp>(brain, IApp.Key(Brain, "echo")), RuntimeBrain.Requested);

        var pending = Assert.Single(await Edge(brain).PendingBatches(session));
        Assert.Equal(subscription, pending.Subscription);
        Assert.Equal([first, second], pending.Signals.Select(SignalJson.RequestId).ToArray());
        // Seeding is durable: a reactivated runtime still holds the backlog.
        await brain.DeactivateAsync(Runtime(brain), ct);
        var resumed = (await Runtime(brain).Inspect(ct)).SessionId!;
        Assert.Equal(2, Assert.Single(await Edge(brain).PendingBatches(resumed)).Signals.Length);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ANeuronThatDoesNotRetainLeavesANewSubscriptionUnseeded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartWithAppsAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var pinger = brain.Get<IPinger>(Brain + "/pinger");

        await Edge(brain).Subscribed(session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);

        Assert.Empty(await Edge(brain).PendingBatches(session));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AReSubscribingSynapseDoesNotReceiveRetainedRequestsItAcknowledged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartWithAppsAsync(new FakeSessionHost(), ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "echo"));
        await app.Create(new AppManifest("Echo", "", [new("open", "")], [], []), "spec");
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await app.Request("open", "one");
        var subscription = await Edge(brain).Subscribed(session, Behavior, RuntimeBrain.Plain<IApp>(brain, IApp.Key(Brain, "echo")), RuntimeBrain.Requested);
        Assert.Single(Assert.Single(await Edge(brain).PendingBatches(session)).Signals);

        await Edge(brain).Acknowledge(session, subscription, 1);
        var again = await Edge(brain).Subscribed(session, Behavior, RuntimeBrain.Plain<IApp>(brain, IApp.Key(Brain, "echo")), RuntimeBrain.Requested);

        Assert.Equal(subscription, again);
        Assert.Empty(await Edge(brain).PendingBatches(session));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task The65thRequestEvictsTheOldestSeededOneAndCountsItDropped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartWithAppsAsync(new FakeSessionHost(), ct);
        var app = brain.Get<IApp>(IApp.Key(Brain, "echo"));
        await app.Create(new AppManifest("Echo", "", [new("open", "")], [], []), "spec");
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var first = await app.Request("open", "0");
        for (var i = 1; i < 65; i++) { await app.Request("open", i.ToString()); }
        await Edge(brain).Subscribed(session, Behavior, RuntimeBrain.Plain<IApp>(brain, IApp.Key(Brain, "echo")), RuntimeBrain.Requested);
        Assert.Equal(64, Assert.Single(await Edge(brain).PendingBatches(session)).Signals.Length);
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);

        await app.Request("open", "65");
        await changes.NextAsync(ct: ct);

        var pending = Assert.Single(await Edge(brain).PendingBatches(session));
        Assert.Equal(64, pending.Signals.Length);
        Assert.DoesNotContain(first, pending.Signals.Select(SignalJson.RequestId).ToArray());
        var sub = Assert.Single(Assert.Single((await Runtime(brain).Inspect(ct)).Behaviors).Subscriptions,
            s => s.Neuron == RuntimeBrain.Plain<IApp>(brain, IApp.Key(Brain, "echo")));
        Assert.Equal(1, sub.Dropped);
    }

    private static IBrainPrograms Programs(ITestBrain brain) => RuntimeBrain.Programs(brain, Brain);

    private static IBrainRuntime Runtime(ITestBrain brain) => RuntimeBrain.Runtime(brain, Brain);

    private static IBrainRuntimeEdge Edge(ITestBrain brain) => RuntimeBrain.Edge(brain, Brain);
}
