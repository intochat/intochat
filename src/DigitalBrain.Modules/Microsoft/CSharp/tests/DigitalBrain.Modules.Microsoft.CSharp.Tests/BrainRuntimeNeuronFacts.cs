using DigitalBrain.Client.Runtime;
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

    [Fact]
    public async Task AssigningTheFirstBehaviorStartsASessionAndAnEmptySetStopsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);

        var started = await host.NextAsync(FakeSessionHost.IsStart, ct);
        Assert.Equal("http://edge.test/brains/acc-brain/", started.Body!["edgeUrl"]!.GetValue<string>());
        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
        await Edge(brain).Retire("acc-brain/csharp/a");
        await host.NextAsync(FakeSessionHost.IsStop, ct);
        Assert.Equal(SessionStatus.None, (await Runtime(brain).Inspect(ct)).Session);
    }

    [Fact]
    public async Task AnAbsentSandboxLeavesTheSessionStartingForTheReminderToRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost { Absent = true };
        await using var brain = await RuntimeBrain.StartAsync(host, ct);

        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);

        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
        Assert.Equal(CSharpFileStatus.Starting, (await Edge(brain).Describe("acc-brain/csharp/a"))!.Status);
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
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;

        var assignment = Assert.Single(await Edge(brain).Assignments(session));

        Assert.Equal("http://host.docker.internal:10000/devstoreaccount1/behaviors/hash-a?sig=x", assignment.Assembly.ToString());
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
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var fingerprint = brain.SiloServices.GetRequiredService<ScriptContracts>().Fingerprint;

        await Edge(brain).Connected(session, fingerprint + "\nExtra.Contracts=00");
        Assert.Equal(SessionStatus.Connected, (await Runtime(brain).Inspect(ct)).Session);

        await Edge(brain).Connected(session, "DigitalBrain=stale");
        Assert.Equal(SessionStatus.Refused, (await Runtime(brain).Inspect(ct)).Session);
        Assert.False((await Edge(brain).Authorize(session)).Active);
    }

    [Fact]
    public async Task ASignalIsBufferedUntilAcknowledgedAndResentOnTheNextStream()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var pinger = brain.Get<IPinger>("pinger");
        var subscription = await Edge(brain).Subscribed(session, "acc-brain/csharp/a", pinger.GetGrainId().ToString(), nameof(Pinged));
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

    // A reloaded behavior subscribes again; what waited for it while it was down is pushed at once, not on the next arrival.
    [Fact(Timeout = 60_000)]
    public async Task AResubscribingBehaviorGetsWhatIsPendingForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await Edge(brain).Connected(session, brain.SiloServices.GetRequiredService<ScriptContracts>().Fingerprint);
        var pinger = brain.Get<IPinger>("pinger");
        var subscription = await Edge(brain).Subscribed(session, "acc-brain/csharp/a", pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await pinger.Ping(1);
        await changes.NextAsync(ct: ct);
        // Queued behind the arrival's turn: once it answers, the arrival's own push (to no stream) is over.
        Assert.Single(await Edge(brain).PendingBatches(session));
        var stream = brain.SiloServices.GetRequiredService<DigitalBrain.Microsoft.CSharp.Sessions.SessionStreams>().Open(session);

        await Edge(brain).Subscribed(session, "acc-brain/csharp/a", pinger.GetGrainId().ToString(), nameof(Pinged));

        Assert.True(stream.Reader.TryRead(out var pushed));
        Assert.Equal(DigitalBrain.Client.Runtime.V1.SignalBatchEvent.Kind, pushed.Kind);
        Assert.Equal(subscription, ((DigitalBrain.Client.Runtime.V1.SignalBatchEvent)pushed.Payload).Subscription);
    }

    [Fact]
    public async Task AFailingBehaviorDisarmsAloneAfterFiveCrashes()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        await Edge(brain).Assign("acc-brain/csharp/b", "hash-b", [], null, null);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;

        for (var crash = 0; crash < 5; crash++) { await Edge(brain).Reported(session, new("acc-brain/csharp/a", "hash-a", "Exited", 1, "boom")); }

        Assert.Equal(CSharpFileStatus.Disarmed, (await Edge(brain).Describe("acc-brain/csharp/a"))!.Status);
        Assert.Equal(CSharpFileStatus.Starting, (await Edge(brain).Describe("acc-brain/csharp/b"))!.Status);
        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
        Assert.DoesNotContain(host.Requests, request => request.Path.EndsWith("/stop", StringComparison.Ordinal));
        Assert.DoesNotContain(await Edge(brain).Assignments(session), assignment => assignment.Behavior == "acc-brain/csharp/a");
    }

    // Five crashes in a burst disarm; a long-lived behavior that crashes now and then never adds up to five.
    [Fact]
    public async Task AFailureLongAfterTheLastStartsTheCountAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new MovableTime();
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, time: time);
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        for (var crash = 0; crash < 4; crash++) { await Edge(brain).Reported(session, new("acc-brain/csharp/a", "hash-a", "Exited", 1, "boom")); }

        time.Now += TimeSpan.FromMinutes(11);
        await Edge(brain).Heartbeat(session);
        await Edge(brain).Reported(session, new("acc-brain/csharp/a", "hash-a", "Exited", 1, "boom"));

        var behavior = (await Edge(brain).Describe("acc-brain/csharp/a"))!;
        Assert.NotEqual(CSharpFileStatus.Disarmed, behavior.Status);
        Assert.Equal(1, behavior.Failures);
    }

    // A crash is reloaded after a backoff, so a dependency that is down for a few seconds cannot burn
    // every attempt in an instant.
    [Fact(Timeout = 60_000)]
    public async Task ACrashedBehaviorIsReloadedAfterABackoff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var stream = brain.SiloServices.GetRequiredService<DigitalBrain.Microsoft.CSharp.Sessions.SessionStreams>().Open(session);

        await Edge(brain).Reported(session, new("acc-brain/csharp/a", "hash-a", "Exited", 1, "boom"));

        Assert.False(stream.Reader.TryRead(out _));
        var reloaded = await stream.Reader.ReadAsync(ct);
        Assert.Equal(DigitalBrain.Client.Runtime.V1.AssignmentEvent.Kind, reloaded.Kind);
    }

    // The host forgot the session (its container was replaced): the reminder ensures the same session again.
    [Fact(Timeout = 60_000)]
    public async Task AReminderEnsuresTheSessionAgainWhenTheHostLostIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
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
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        var first = await host.NextAsync(FakeSessionHost.IsStart, ct);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        await Edge(brain).Connected(session, brain.SiloServices.GetRequiredService<ScriptContracts>().Fingerprint);
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
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
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
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        await host.NextAsync(FakeSessionHost.IsStart, ct);
        host.HoldStops = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await Edge(brain).Retire("acc-brain/csharp/a");
        await host.NextAsync(FakeSessionHost.IsStop, ct);
        await Edge(brain).Assign("acc-brain/csharp/b", "hash-b", [], null, null);
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
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
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

        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default);

        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
    }

    [Fact]
    public async Task AHostThatReportsMoreAssembliesStillCoversTheSilosContracts()
    {
        var silo = ContractsFingerprint.Of([typeof(Signal).Assembly]);
        Assert.True(ContractsFingerprint.Covers(ContractsFingerprint.Of([typeof(Signal).Assembly, typeof(ContractsFingerprint).Assembly]), silo));
        Assert.False(ContractsFingerprint.Covers(ContractsFingerprint.Of([typeof(ContractsFingerprint).Assembly]), silo));
        await Task.CompletedTask;
    }

    private static IBrainRuntime Runtime(ModuleBrain brain) => brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(Brain));

    private static IBrainRuntimeEdge Edge(ModuleBrain brain) => brain.Grains().GetGrain<IBrainRuntimeEdge>(BrainRuntimeKeys.For(Brain));
}
