using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Microsoft.CSharp.Edge;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class BrainRuntimeNeuronFacts
{
    private const string Brain = "acc-brain";
    private const string Owner = "acc-brain/apps/x";
    private const string Behavior = "acc-brain/apps/x/synapses/a.cs";

    private static Dictionary<string, string> Synapse(string source = RuntimeBrain.Idle)
        => new(StringComparer.Ordinal) { ["synapses/a.cs"] = source };

    [Fact]
    public async Task AcknowledgementBatchesPersistIndependentWatermarksAndRejectInvalidBatches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var runtime = Edge(brain);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var one = brain.Get<IPinger>("one");
        var two = brain.Get<IPinger>("two");
        var first = await runtime.Subscribed(session, Behavior, one.GetGrainId().ToString(), nameof(Pinged));
        var second = await runtime.Subscribed(session, Behavior, two.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await one.Ping(1); await changes.NextAsync(ct: ct);
        await one.Ping(2); await changes.NextAsync(ct: ct);
        await two.Ping(3); await changes.NextAsync(ct: ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runtime.AcknowledgeMany("stale", [new(first, 2)]));
        foreach (var invalid in new AcknowledgeReport[][] { [], [new(first, 2), null!], [new(first, -1)],
            [new("", 1)], Enumerable.Repeat(new AcknowledgeReport(first, 1), RuntimeProtocol.MaximumAcknowledgements + 1).ToArray() })
        { await Assert.ThrowsAnyAsync<ArgumentException>(() => runtime.AcknowledgeMany(session, invalid)); }
        Assert.Equal(3, (await runtime.PendingBatches(session)).Sum(batch => batch.Signals.Length));
        await runtime.AcknowledgeMany(session, [new(first, 1), new(second, 0), new("retired", 99), new(first, 1)]);
        var pending = await runtime.PendingBatches(session);
        Assert.Equal(2, pending.Count);
        Assert.Equal(2, SignalJson.Number(Assert.Single(pending.Single(batch => batch.Subscription == first).Signals)));
        Assert.Equal(3, SignalJson.Number(Assert.Single(pending.Single(batch => batch.Subscription == second).Signals)));
        await brain.DeactivateAsync(Runtime(brain), ct);
        var resumed = (await Runtime(brain).Inspect(ct)).SessionId!;
        Assert.Equal(2, (await runtime.PendingBatches(resumed)).Sum(batch => batch.Signals.Length));
        await runtime.AcknowledgeMany(resumed, [new(first, 2), new(second, 1)]);
        await brain.DeactivateAsync(Runtime(brain), ct);
        resumed = (await Runtime(brain).Inspect(ct)).SessionId!;
        Assert.Empty(await runtime.PendingBatches(resumed));
    }

    [Fact]
    public async Task AssigningTheFirstBehaviorStartsASessionAndAnEmptySetStopsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await Programs(brain).Assign(Owner, Synapse(), []);

        var started = await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.Equal("http://edge.test/brains/acc-brain/", started.Body!["edgeUrl"]!.GetValue<string>());
        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
        await Programs(brain).Retire(Owner);
        await host.NextAsync(FakeSessionHost.IsStop, ct);
        Assert.Equal(SessionStatus.None, (await Runtime(brain).Inspect(ct)).Session);
    }

    [Fact]
    public async Task AnAbsentSandboxLeavesTheSessionStartingForTheReminderToRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost { Absent = true };
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await Programs(brain).Assign(Owner, Synapse(), []);

        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
        Assert.Equal("Starting", (await Programs(brain).Describe(Owner))!.Behaviors[0].Status);
        host.Absent = false;
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
    }

    // Local blob storage listens on the developer's loopback, which a sandbox container cannot reach.
    [Fact]
    public async Task ALoopbackAssemblyUrlReachesTheHostThroughTheDockerHost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, assemblies: new LoopbackAssemblies());
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;

        var assignment = Assert.Single(await Edge(brain).Assignments(session));

        Assert.Equal("host.docker.internal", assignment.Assembly.Host);
        Assert.Equal("host.docker.internal", assignment.Symbols.Host);
    }

    private sealed class LoopbackAssemblies : DigitalBrain.Microsoft.CSharp.Assemblies.IBehaviorAssemblies
    {
        public Task Put(DigitalBrain.Microsoft.CSharp.Catalog.CompiledBehavior behavior, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> Exists(string hash, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<Uri> ReadUrl(string name, TimeSpan lifetime, CancellationToken cancellationToken)
            => Task.FromResult(new Uri("http://127.0.0.1:10000/devstoreaccount1/behaviors/" + name + "?sig=x"));
    }

    [Fact]
    public async Task TouchWithNothingAssignedStartsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await Runtime(brain).Touch(ct);

        Assert.DoesNotContain(host.Requests, FakeSessionHost.IsStart);
    }

    [Fact]
    public async Task AMatchingFingerprintConnectsAndAMismatchRefuses()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var fingerprint = ContractsFingerprint.Of(brain.SiloServices.GetRequiredService<DigitalBrain.Framework.CompiledTypes>().Assemblies);

        await Edge(brain).Connected(session, fingerprint + "\nExtra.Contracts=00");
        Assert.Equal(SessionStatus.Connected, (await Runtime(brain).Inspect(ct)).Session);

        await Edge(brain).Connected(session, "DigitalBrain=stale");
        Assert.Equal(SessionStatus.Refused, (await Runtime(brain).Inspect(ct)).Session);
        Assert.False((await Edge(brain).Authorize(session)).Active);
    }

    [Fact]
    public async Task AReplacedSessionCannotResolveAStillAssignedBehavior()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, time: time);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var stale = (await Runtime(brain).Inspect(ct)).SessionId!;
        Assert.NotNull(await Edge(brain).Identity(stale, Behavior));

        time.Now += TimeSpan.FromMinutes(3);
        await Runtime(brain).Touch(ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Edge(brain).Identity(stale, Behavior));
        var current = (await Runtime(brain).Inspect(ct)).SessionId!;
        Assert.NotNull(await Edge(brain).Identity(current, Behavior));
        Assert.Null(await Edge(brain).Identity(current, "acc-brain/apps/x/synapses/unassigned.cs"));
    }

    [Fact]
    public async Task ASignalIsBufferedUntilAcknowledgedAndResentOnTheNextStream()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var pinger = brain.Get<IPinger>("pinger");
        var subscription = await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);

        await pinger.Ping(1);
        await changes.NextAsync(ct: ct);

        var pending = Assert.Single(await Edge(brain).PendingBatches(session));
        Assert.Equal((subscription, 1L), (pending.Subscription, pending.Watermark));
        Assert.Equal(1, SignalJson.Number(Assert.Single(pending.Signals)));
        Assert.Single(await Edge(brain).PendingBatches(session));   // reading does not clear it
        await Edge(brain).Acknowledge(session, subscription, 1);
        Assert.Empty(await Edge(brain).PendingBatches(session));
    }

    // An older program's subscription can name a neuron type the host no longer carries; it must not
    // keep every other source from being watched.
    [Fact(Timeout = 60_000)]
    public async Task ASourceThatCannotBeWatchedDoesNotStarveTheOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await Assert.ThrowsAnyAsync<Exception>(() => Edge(brain).Subscribed(session, Behavior, "revisions.index/acc-brain", nameof(Pinged)));
        var pinger = brain.Get<IPinger>("pinger");

        var subscription = await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await pinger.Ping(1);
        await changes.NextAsync(ct: ct);

        Assert.Equal(subscription, Assert.Single(await Edge(brain).PendingBatches(session)).Subscription);
    }

    // A reloaded behavior subscribes again; what waited for it while it was down is pushed at once, not on the next arrival.
    [Fact(Timeout = 60_000)]
    public async Task AResubscribingBehaviorGetsWhatIsPendingForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await Edge(brain).Connected(session, ContractsFingerprint.Of(brain.SiloServices.GetRequiredService<DigitalBrain.Framework.CompiledTypes>().Assemblies));
        var pinger = brain.Get<IPinger>("pinger");
        var subscription = await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await pinger.Ping(1);
        await changes.NextAsync(ct: ct);
        // Queued behind the arrival's turn: once it answers, the arrival's own push (to no stream) is over.
        Assert.Single(await Edge(brain).PendingBatches(session));
        var stream = brain.SiloServices.GetRequiredService<DigitalBrain.Microsoft.CSharp.Sessions.SessionStreams>().Open(session);

        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));

        Assert.True(stream.Reader.TryRead(out var pushed));
        Assert.Equal(DigitalBrain.Client.Runtime.V1.SignalBatchEvent.Kind, pushed.Kind);
        Assert.Equal(subscription, ((DigitalBrain.Client.Runtime.V1.SignalBatchEvent)pushed.Payload).Subscription);
    }

    // The host forgot the session (its container was replaced): the reminder ensures the same session again.
    [Fact(Timeout = 60_000)]
    public async Task AReminderEnsuresTheSessionAgainWhenTheHostLostIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var first = await host.NextAsync(FakeSessionHost.IsStart, ct);
        host.Sessions.Clear();

        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default);

        var again = await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.Equal(first.Body!["session"]!.GetValue<string>(), again.Body!["session"]!.GetValue<string>());
    }

    [Fact(Timeout = 30_000)]
    public async Task ReactivationRelaunchesBehaviorsWithoutWaitingForAReminder()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(host, ct, time: time);
        await Programs(brain).Assign(Owner, Synapse(), []);
        var first = await host.NextAsync(FakeSessionHost.IsStart, ct);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await Edge(brain).Connected(session, ContractsFingerprint.Of(brain.SiloServices.GetRequiredService<DigitalBrain.Framework.CompiledTypes>().Assemblies));
        await brain.DeactivateAsync(Runtime(brain), ct);
        time.Now += TimeSpan.FromSeconds(30);

        var resumed = await Runtime(brain).Inspect(ct);
        var again = await host.NextAsync(FakeSessionHost.IsStart, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.NotEqual(session, resumed.SessionId);
        Assert.Equal(resumed.SessionId, again.Body!["session"]!.GetValue<string>());
        Assert.NotEqual(first.Body!["token"]!.GetValue<string>(), again.Body["token"]!.GetValue<string>());
        Assert.False((await Edge(brain).Authorize(session)).Active);
        Assert.Single(resumed.Behaviors);
    }

    // Heartbeats are the detector: a session silent past the limit is replaced, and its token stops speaking.
    [Fact(Timeout = 60_000)]
    public async Task ASilentSessionIsReplacedByANewOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(host, ct, time: time);
        await Programs(brain).Assign(Owner, Synapse(), []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        var silent = (await Runtime(brain).Inspect(ct)).SessionId!;

        time.Now += TimeSpan.FromMinutes(3);
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default);

        var relaunched = await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.NotEqual(silent, relaunched.Body!["session"]!.GetValue<string>());
        Assert.False((await Edge(brain).Authorize(silent)).Active);
    }

    // Sandbox calls leave the runtime's turn, but not their order: a stop still in flight must not land
    // after the start that follows it and take the new session down.
    [Fact(Timeout = 60_000)]
    public async Task AStopReachesTheHostBeforeTheStartThatFollowsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign("acc-brain/apps/a", Synapse(), []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        host.HoldStops = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await Programs(brain).Retire("acc-brain/apps/a");
        await host.NextAsync(FakeSessionHost.IsStop, ct);
        await Programs(brain).Assign("acc-brain/apps/b", Synapse(), []);
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        host.HoldStops.SetResult();

        await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.False(host.StartDuringStop);
    }

    // A rotation pushed while the host's stream is down never reached it; the next reminder rotates again.
    [Fact(Timeout = 60_000)]
    public async Task ATokenRotationTheStreamMissedIsRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(host, ct, time: time);
        await Programs(brain).Assign(Owner, Synapse(), []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        time.Now += TimeSpan.FromMinutes(41);
        await Edge(brain).Heartbeat(session);
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default);

        var stream = brain.SiloServices.GetRequiredService<DigitalBrain.Microsoft.CSharp.Sessions.SessionStreams>().Open(session);
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default);

        Assert.True(stream.Reader.TryRead(out var pushed));
        Assert.Equal(DigitalBrain.Client.Runtime.V1.TokenEvent.Kind, pushed.Kind);
    }

    // A sandbox endpoint that accepts connections and never answers must not hold the runtime's turn.
    [Fact(Timeout = 30_000)]
    public async Task AHangingSandboxNeverHoldsTheRuntime()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost { Hang = true };
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await Programs(brain).Assign(Owner, Synapse(), []);
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default);

        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
    }

    [Fact]
    public async Task AssigningAProgramCompilesEverySynapseAndStartsOneSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        var view = await Programs(brain).Assign("acc-brain/apps/demo",
            new() { ["synapses/a.cs"] = RuntimeBrain.Idle, ["synapses/b.cs"] = RuntimeBrain.Idle, ["lib/Shared.cs"] = "public static class Shared {}\n" },
            new() { ["App"] = "acc-brain/apps/demo" });

        Assert.Equal(["acc-brain/apps/demo/synapses/a.cs", "acc-brain/apps/demo/synapses/b.cs"], view.Behaviors.Select(b => b.Id));
        Assert.All(view.Behaviors, behavior => Assert.Equal("Starting", behavior.Status));
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var assignments = await Edge(brain).Assignments(session);
        Assert.Equal(2, assignments.Count);
        Assert.All(assignments, assignment => Assert.Equal("acc-brain/apps/demo", assignment.Settings["App"]));
    }

    [Fact]
    public async Task AProgramThatDoesNotCompileIsRefusedAndTheRunningOneIsUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        var before = await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Programs(brain).Assign("acc-brain/apps/demo",
            new() { ["synapses/a.cs"] = RuntimeBrain.Idle, ["synapses/b.cs"] = "int x = \"no\";\n" }, []));

        Assert.Contains("synapses/b.cs", error.Message, StringComparison.Ordinal);
        Assert.Contains("CS0029", error.Message, StringComparison.Ordinal);
        Assert.Equal(before.Hash, (await Programs(brain).Describe("acc-brain/apps/demo"))!.Hash);
        Assert.Single((await Programs(brain).Describe("acc-brain/apps/demo"))!.Behaviors);
    }

    [Fact]
    public async Task ReassigningWithoutASynapseRetiresOnlyThatBehaviorAndKeepsTheOthersSubscriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle, ["synapses/b.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var source = brain.Get<IPinger>("one2");
        var kept = await Edge(brain).Subscribed(session, "acc-brain/apps/demo/synapses/a.cs", source.GetGrainId().ToString(), nameof(Pinged));
        await Edge(brain).Subscribed(session, "acc-brain/apps/demo/synapses/b.cs", source.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await source.Ping(1); await changes.NextAsync(ct: ct);

        var view = await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle + "// v2\n" }, []);

        Assert.Equal(["acc-brain/apps/demo/synapses/a.cs"], view.Behaviors.Select(b => b.Id));
        var pending = Assert.Single(await Edge(brain).PendingBatches(session));
        Assert.Equal(kept, pending.Subscription);
        Assert.Single(pending.Signals);
    }

    [Fact]
    public async Task RetiringTheLastProgramStopsTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        await host.NextAsync(FakeSessionHost.IsStart, ct);

        await Programs(brain).Retire("acc-brain/apps/demo");

        await host.NextAsync(FakeSessionHost.IsStop, ct);
        Assert.Null(await Programs(brain).Describe("acc-brain/apps/demo"));
        Assert.Equal(SessionStatus.None, (await Runtime(brain).Inspect(ct)).Session);
    }

    [Fact]
    public async Task IdentityAnswersThePrincipalAndOwnerOfABehavior()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;

        var identity = await Edge(brain).Identity(session, "acc-brain/apps/demo/synapses/a.cs");

        Assert.NotNull(identity);
        Assert.Equal("acc-brain/apps/demo", identity!.Owner);
        Assert.Null(await Edge(brain).Identity(session, "acc-brain/apps/other/synapses/a.cs"));
    }

    [Fact]
    public async Task AReportedExitIsRememberedForDescribeAndNeverDisarms()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var view = await Programs(brain).Assign("acc-brain/apps/demo", new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var hash = view.Behaviors[0].Hash;

        for (var attempt = 0; attempt < 10; attempt++)
        { await Edge(brain).Reported(session, new("acc-brain/apps/demo/synapses/a.cs", hash, "Exited", 1, "boom")); }

        var described = Assert.Single((await Programs(brain).Describe("acc-brain/apps/demo"))!.Behaviors);
        Assert.Equal("Exited", described.Status);
        Assert.Equal("boom", described.Error);
        Assert.Single(await Edge(brain).Assignments(session));   // still assigned, never disarmed
    }

    [Fact]
    public async Task AHostThatReportsMoreAssembliesStillCoversTheSilosContracts()
    {
        var silo = ContractsFingerprint.Of([typeof(Signal).Assembly]);
        Assert.True(ContractsFingerprint.Covers(ContractsFingerprint.Of([typeof(Signal).Assembly, typeof(ContractsFingerprint).Assembly]), silo));
        Assert.False(ContractsFingerprint.Covers(ContractsFingerprint.Of([typeof(ContractsFingerprint).Assembly]), silo));
        await Task.CompletedTask;
    }

    private static IBrainPrograms Programs(ModuleBrain brain) => brain.Grains.GetGrain<IBrainPrograms>(BrainRuntimeKeys.For(Brain));

    private static IBrainRuntime Runtime(ModuleBrain brain) => brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(Brain));

    private static IBrainRuntimeEdge Edge(ModuleBrain brain) => brain.Grains.GetGrain<IBrainRuntimeEdge>(BrainRuntimeKeys.For(Brain));
}
