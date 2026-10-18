using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests.Integration;

// AppNeuron retains AppRequested; a synapse that subscribes after its app's requests were published
// still receives them because Subscribed seeds the new subscription's backlog from that retention.
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
        var stream = brain.Services.GetRequiredService<DigitalBrain.Microsoft.CSharp.Sessions.SessionStreams>().Open(session);

        var subscription = await Edge(brain).Subscribed(session, Behavior, app.GetGrainId().ToString(), nameof(AppRequested));

        Assert.True(stream.Reader.TryRead(out var pushed), "The retained seed must reach the live stream without another signal or reconnect.");
        var batch = Assert.IsType<DigitalBrain.Client.Runtime.V1.SignalBatchEvent>(pushed.Payload);
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

        var subscription = await Edge(brain).Subscribed(session, Behavior, app.GetGrainId().ToString(), nameof(AppRequested));

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
        var pinger = brain.Get<IPinger>("pinger");

        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));

        Assert.Empty(await Edge(brain).PendingBatches(session));
    }

    // Whether a source retains is its grain type's to say, from the cluster's manifest: a source that does not is
    // never asked, rather than answering a failed cast across the cluster for every subscription to it.
    [Trait("host", "own")]
    [Fact]
    public async Task OnlyASourceThatRetainsIsAskedForABacklog()
    {
        var ct = TestContext.Current.CancellationToken;
        var asked = new RetentionCalls();
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost())
            .ConfigureServices(services => services.AddSingleton<IOutgoingGrainCallFilter>(asked)).StartAsync(ct);
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var pinger = brain.Get<IPinger>("silent");
        var retainer = brain.Get<ISlowRetainer>("asked");

        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await Edge(brain).Subscribed(session, Behavior, retainer.GetGrainId().ToString(), nameof(Pinged));

        Assert.Equal([retainer.GetGrainId().ToString()], asked.Targets);
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
        var subscription = await Edge(brain).Subscribed(session, Behavior, app.GetGrainId().ToString(), nameof(AppRequested));
        Assert.Single(Assert.Single(await Edge(brain).PendingBatches(session)).Signals);

        await Edge(brain).Acknowledge(session, subscription, 1);
        var again = await Edge(brain).Subscribed(session, Behavior, app.GetGrainId().ToString(), nameof(AppRequested));

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
        await Edge(brain).Subscribed(session, Behavior, app.GetGrainId().ToString(), nameof(AppRequested));
        Assert.Equal(64, Assert.Single(await Edge(brain).PendingBatches(session)).Signals.Length);
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);

        await app.Request("open", "65");
        await changes.NextAsync(ct: ct);

        var pending = Assert.Single(await Edge(brain).PendingBatches(session));
        Assert.Equal(64, pending.Signals.Length);
        Assert.DoesNotContain(first, pending.Signals.Select(SignalJson.RequestId).ToArray());
        var sub = Assert.Single(Assert.Single((await Runtime(brain).Inspect(ct)).Behaviors).Subscriptions,
            s => s.Neuron == app.GetGrainId().ToString());
        Assert.Equal(1, sub.Dropped);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASubscriptionIsSeededWhileItsSourceIsInALongTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartWithAppsAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var source = brain.Get<ISlowRetainer>("slow");
        var turn = source.HoldThenPublish();
        await SlowRetainer.Entered.Task.WaitAsync(ct);

        await Edge(brain).Subscribed(session, Behavior, source.GetGrainId().ToString(), nameof(Pinged));

        SlowRetainer.Release.SetResult();
        await turn;
    }

    // Every backlog request the brain's grains send, by target.
    private sealed class RetentionCalls : IOutgoingGrainCallFilter
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _targets = new();

        public string[] Targets => [.. _targets];

        public Task Invoke(IOutgoingGrainCallContext context)
        {
            if (context.InterfaceMethod?.DeclaringType == typeof(IRetainsSignals)) { _targets.Enqueue(context.TargetId.ToString()); }
            return context.Invoke();
        }
    }

    private static IBrainPrograms Programs(ITestBrain brain) => RuntimeBrain.Programs(brain, Brain);

    private static IBrainRuntime Runtime(ITestBrain brain) => RuntimeBrain.Runtime(brain, Brain);

    private static IBrainRuntimeEdge Edge(ITestBrain brain) => RuntimeBrain.Edge(brain, Brain);
}
// A source that retains and is busy: its turn holds until released, the way a neuron mid-publish waits
// on the runtime.
[Alias("tests.csharp.slow-retainer")]
public interface ISlowRetainer : IRetainsSignals
{
    Task HoldThenPublish();
}

public sealed class SlowRetainer : Neuron, ISlowRetainer
{
    public static TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<RetainedSignal[]> Retained(string signal, long since) => Task.FromResult<RetainedSignal[]>([]);

    public async Task HoldThenPublish()
    {
        Entered.SetResult();
        await Release.Task;
        await PublishAsync(new Pinged(1));
    }
}
