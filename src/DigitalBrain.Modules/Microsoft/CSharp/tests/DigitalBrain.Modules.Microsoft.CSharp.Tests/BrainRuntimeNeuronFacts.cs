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

        var started = Assert.Single(host.Requests, FakeSessionHost.IsStart);
        Assert.Equal("http://edge.test/brains/acc-brain/", started.Body!["edgeUrl"]!.GetValue<string>());
        Assert.Equal(SessionStatus.Starting, (await Runtime(brain).Inspect(ct)).Session);
        await Edge(brain).Retire("acc-brain/csharp/a");
        Assert.Contains(host.Requests, request => request.Path.EndsWith("/stop", StringComparison.Ordinal));
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
        Assert.Single(host.Requests, FakeSessionHost.IsStart);
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

    [Fact]
    public async Task AReminderRelaunchesASessionTheHostLost()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(host, ct);
        await Edge(brain).Assign("acc-brain/csharp/a", "hash-a", [], null, null);
        var first = (await Runtime(brain).Inspect(ct)).SessionId;
        host.Sessions.Clear();

        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default);

        Assert.Equal(2, host.Requests.Count(FakeSessionHost.IsStart));
        Assert.NotEqual(first, (await Runtime(brain).Inspect(ct)).SessionId);
        Assert.False((await Edge(brain).Authorize(first!)).Active);
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
