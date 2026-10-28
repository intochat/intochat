using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime.Signals;
using IntoChat.OS.Modules.Microsoft.CSharp.Sessions;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// A synapse's runs are facts the brain's runs neuron publishes: a batch on a live stream starts a run, an
// acknowledgement that moves the subscription finishes it, an exit with batches in flight fails it. The runs neuron and every source
// are plain neurons (06-08): what the runs neuron published is read from the brain's log, and a plain signal carries no trace, so
// the traces the legacy journal carried from a source's signal to a run are gone (the runs record a turn's trace only).
public sealed class RunFactsFacts
{
    private const string Acc = "acc-brain";
    private const string Owner = "acc-brain/apps/x";
    private const string Behavior = "acc-brain/apps/x/synapses/a.cs";
    private const string Reaction = "acc-brain/apps/x/synapses/runs.cs";
    private const string Other = "other-brain";
    private const string OtherOwner = "other-brain/apps/y";
    private const string OtherBehavior = "other-brain/apps/y/synapses/a.cs";
    private static readonly Trace First = new() { TraceId = "4bf92f3577b34da6a3ce929d0e0e4736", PayerAccountId = "account-a", BrainId = Acc, Origin = new NeuronId("acc-brain/edge") };
    private static readonly Trace Second = new() { TraceId = "0af7651916cd43dd8448eb211c80319c", PayerAccountId = "account-a", BrainId = Acc, Origin = new NeuronId("acc-brain/edge") };

    [Trait("host", "own")]
    [Fact]
    public async Task HistoryInvalidatesOnlyWhenItsDurableSnapshotChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/history-invalidation");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await using var changes = await rig.Brain.Observe<BrainRunsChanged>(rig.Runs, ct);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        Assert.Empty(await rig.Facts<BrainRunsChanged>(ct));
        await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 1, WallMicroseconds: 1_500)]);
        await rig.Ran.NextAsync(ct: ct);
        Assert.Single(await rig.Facts<BrainRunsChanged>(ct));
        var change = await changes.NextAsync(ct: ct);
        Assert.Equal((await rig.Runs.Read()).Revision, change.Revision);
        await PingAsync(pinger, 2, Second);
        await rig.Started.NextAsync(ct: ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 2, Skipped: true)]);
        await rig.Ran.NextAsync(ct: ct);
        // Both completed/skipped facts remain observable, but skipped work is not history.
        await TestWait.UntilAsync(async token => (await rig.Facts<BehaviorRan>(token)).Length, count => count == 2, TimeSpan.FromSeconds(10), ct);
        Assert.Single(await rig.Facts<BrainRunsChanged>(ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RunSnapshotsSurviveReactivationAndReplayDoesNotChangeStatistics()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/snapshot");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 1, WallMicroseconds: 1_500)]);
        await rig.Ran.NextAsync(ct: ct);
        var before = await rig.Runs.Read();
        Assert.Equal(1_500, Assert.Single(before.Statistics).P50Microseconds);
        await rig.Brain.DeactivateAsync(rig.Runs, ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 1, WallMicroseconds: 1_500)]);
        var after = await rig.Runs.Read();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Recent, after.Recent);
        Assert.Single(Assert.Single(after.Statistics).Samples);
        Assert.Empty((await rig.Runs.Read(Owner)).Recent);
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Runs.Read(OtherOwner));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task IdleWaitsForAcknowledgedRunPricingAfterSubscriptionsDrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var assignment = Assert.Single(await rig.Edge.Assignments(rig.Session));
        await rig.Edge.Reported(rig.Session, new(Behavior, assignment.Hash, "Initialized", null, null, assignment.Revision));
        var pinger = rig.Brain.Get<IPinger>(Acc + "/idle-pricing");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        Assert.True((await rig.Runtime.Inspect(ct)).IsIdle);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Meter.Held = release.Task;
        try
        {
            await PingAsync(pinger, 1, First);
            await rig.Started.NextAsync(ct: ct);
            await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, CpuMicroseconds: 300)]);
            var state = await rig.Runtime.Inspect(ct);
            Assert.True(state.IsReady);
            Assert.False(state.IsIdle);
        }
        finally { release.TrySetResult(); }
        await TestWait.UntilAsync(rig.Runtime.Inspect, state => state.IsIdle, TimeSpan.FromSeconds(10), ct);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task IdleWaitsForAcknowledgedRunHistoryToCommitWithoutBlockingTheRuntime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var assignment = Assert.Single(await rig.Edge.Assignments(rig.Session));
        await rig.Edge.Reported(rig.Session, new(Behavior, assignment.Hash, "Initialized", null, null, assignment.Revision));
        var pinger = rig.Brain.Get<IPinger>(Acc + "/idle-history");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        var storage = rig.Held;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.HeldRuns = release.Task;
        storage.RunsWriteEntered = entered;
        try
        {
            await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 1, WallMicroseconds: 1_500)])
                .WaitAsync(TimeSpan.FromSeconds(10), ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var state = await rig.Runtime.Inspect(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.True(state.IsReady);
            Assert.False(state.IsIdle);
        }
        finally { release.TrySetResult(); }
        await TestWait.UntilAsync(rig.Runtime.Inspect, state => state.IsIdle, TimeSpan.FromSeconds(10), ct);
        Assert.Equal(1_500, Assert.Single((await rig.Runs.Read()).Recent).DurationMicroseconds);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnAcknowledgedBatchProducesOneBehaviorRan()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);

        await PingAsync(pinger, 1, First);
        var started = await rig.Started.NextAsync(ct: ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, CpuMicroseconds: 300, WallMicroseconds: 1_500)]);
        var ran = await rig.Ran.NextAsync(ct: ct);

        var view = Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors);
        Assert.Equal((RunOutcome.Succeeded, 1, 1_500L, 300L, 1L, null), (ran.Outcome, ran.Batches, ran.DurationMicroseconds, ran.CpuMicroseconds, ran.Watermark, ran.Error));
        Assert.Equal((BrainRuntimeKeys.For(Acc), Owner, Behavior, view.Hash, RuntimeBrain.Source(pinger), Pings.Pinged),
            (ran.RuntimeId, ran.Owner, ran.Behavior, ran.Hash, ran.Source, ran.SignalType));
        Assert.Equal((Behavior, 1L), (started.Behavior, started.Watermark));
        Assert.Single(await rig.Facts<BehaviorRan>(ct));
        Assert.Equal((1L, 0L, 1_500L), (view.Runs, view.Failures, view.LastDurationMicroseconds));
    }

    // An operation's synapse reads every AppRequested of its app and passes over the others: those batches are
    // delivered and acknowledged, but nothing ran.
    [Trait("host", "own")]
    [Fact]
    public async Task ARequestForAnotherOperationOfTheSameAppIsNotCountedAsARun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);

        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, CpuMicroseconds: 30, WallMicroseconds: 40, Skipped: true)]);
        var ran = await rig.Ran.NextAsync(ct: ct);

        var view = Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors);
        Assert.Equal((RunOutcome.Skipped, 1L), (ran.Outcome, ran.Watermark));
        Assert.Equal((0L, 0L, 0L), (view.Runs, view.Failures, view.LastDurationMicroseconds));
        Assert.Equal(1L, Assert.Single(view.Subscriptions).Delivered);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AHandledRequestIsCountedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);

        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        // A handled and a skipped report for the same subscription in one call: the subscription ran.
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, WallMicroseconds: 70), new AcknowledgeReport(subscription, 1, Skipped: true)]);
        var ran = await rig.Ran.NextAsync(ct: ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, WallMicroseconds: 70)]);

        var view = Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors);
        Assert.Equal(RunOutcome.Succeeded, ran.Outcome);
        Assert.Equal(1L, view.Runs);
        Assert.Single(await rig.Facts<BehaviorRan>(ct));
    }

    // A run is a span of its own, tagged with its synapse: a plain signal carries no trace (06-08), so the batch carries no parent.
    [Trait("host", "own")]
    [Fact]
    public async Task ARunIsASpanTaggedWithItsSynapse()
    {
        var ct = TestContext.Current.CancellationToken;
        var spans = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "IntoChat.OS.Modules.Microsoft.CSharp.Runtime",
            Sample = (ref _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);

        Assert.Null(Assert.Single(await rig.Edge.PendingBatches(rig.Session)).TraceParent);
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, CpuMicroseconds: 300, WallMicroseconds: 1_500)]);
        await rig.Ran.NextAsync(ct: ct);

        Assert.Contains(spans, span => span.OperationName == "runtime.batch" && (string?)span.GetTagItem("intochatos.behavior") == Behavior);
    }

    // A batch's trace is its first signal's, read from the journal: the turn that happens to read it (a nudge, a
    // subscription's own read) lends it nothing, so a signal published under no trace carries none down the stream.
    [Trait("host", "own")]
    [Fact]
    public async Task ABatchCarriesOnlyItsSignalsTraceNeverTheReadingTurns()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/untraced");
        await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        using (new System.Diagnostics.Activity("unrelated").Start()) { await PingAsync(pinger, 1, null); }
        await rig.Started.NextAsync(ct: ct);

        using var reading = new System.Diagnostics.Activity("reading").Start();
        Assert.Null(Assert.Single(await rig.Edge.PendingBatches(rig.Session)).TraceParent);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ARePushedBatchStartsOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);

        // A reloaded behavior subscribes again and is pushed the same batch; a reconnecting stream pulls it.
        await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        Assert.Equal(1, Assert.Single(await rig.Edge.PendingBatches(rig.Session)).Watermark);
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1)]);
        await rig.Ran.NextAsync(ct: ct);

        Assert.Equal(2, Pushed(rig.Stream).Count(batch => batch.Subscription == subscription && batch.Watermark == 1));
        Assert.Single(await rig.Facts<BehaviorStarted>(ct));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnExitWithAnUnackedBatchIsAFailedRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        var hash = Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors).Hash;

        await rig.Edge.Reported(rig.Session, new BehaviorStatusReport(Behavior, hash, "Exited", 1, "boom"));
        var failed = await rig.Ran.NextAsync(ct: ct);

        Assert.Equal((RunOutcome.Failed, 1, 1L, "boom", 0L), (failed.Outcome, failed.Batches, failed.Watermark, failed.Error, failed.DurationMicroseconds));
        var view = Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors);
        Assert.Equal(("Exited", 1L, 1L), (view.Status, view.Runs, view.Failures));
        // The restarted behavior is sent the batch again, and that is a new run.
        Assert.Single(await rig.Edge.PendingBatches(rig.Session));
        Assert.Equal(1, (await rig.Started.NextAsync(ct: ct)).Watermark);
    }

    // A behavior rewritten at its path is new code: the record its old code earned is not its own.
    [Trait("host", "own")]
    [Fact]
    public async Task OnlyARewrittenBehaviorStartsItsRunRecordAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs", "synapses/b.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, WallMicroseconds: 50)]);
        await rig.Ran.NextAsync(ct: ct);
        var programs = RuntimeBrain.Programs(rig.Brain, Acc);
        const string Rewritten = "await using var brain = await IntoChatOSClient.ConnectAsync(args);\nConsole.WriteLine(\"rewritten\");\nawait Task.Delay(Timeout.Infinite, brain.Stopping);\n";

        await programs.Assign(Owner, new(StringComparer.Ordinal) { ["synapses/a.cs"] = RuntimeBrain.Idle, ["synapses/b.cs"] = Rewritten }, []);
        var kept = (await rig.Runtime.Inspect(ct)).Behaviors.Single(view => view.Id == Behavior);
        await programs.Assign(Owner, new(StringComparer.Ordinal) { ["synapses/a.cs"] = Rewritten, ["synapses/b.cs"] = Rewritten }, []);
        var rewritten = (await rig.Runtime.Inspect(ct)).Behaviors.Single(view => view.Id == Behavior);

        Assert.Equal((1L, 0L, 50L), (kept.Runs, kept.Failures, kept.LastDurationMicroseconds));
        Assert.NotEqual(kept.Hash, rewritten.Hash);
        Assert.Equal((0L, 0L, 0L), (rewritten.Runs, rewritten.Failures, rewritten.LastDurationMicroseconds));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task CoalescedAcksSumTheirTimeAndClearEveryTrace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(started => started.Watermark == 1, ct);
        await PingAsync(pinger, 2, Second);
        await rig.Started.NextAsync(started => started.Watermark == 2, ct);

        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, 100, 1_000), new AcknowledgeReport(subscription, 2, 20, 300)]);
        var ran = await rig.Ran.NextAsync(ct: ct);

        Assert.Equal((2, 1_300L, 120L, 2L), (ran.Batches, ran.DurationMicroseconds, ran.CpuMicroseconds, ran.Watermark));
        // Nothing at or below the acknowledged watermark is in flight any more, so an exit now fails no run.
        var hash = Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors).Hash;
        await rig.Edge.Reported(rig.Session, new BehaviorStatusReport(Behavior, hash, "Exited", 1, "after the ack"));
        var view = Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors);
        Assert.Equal((1L, 0L), (view.Runs, view.Failures));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AcknowledgeManySavesOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var one = rig.Brain.Get<IPinger>(Acc + "/one");
        var two = rig.Brain.Get<IPinger>(Acc + "/two");
        var first = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(one), Pings.Pinged);
        var second = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(two), Pings.Pinged);
        await PingAsync(one, 1, First);
        await rig.Started.NextAsync(started => started.Source == RuntimeBrain.Source(one), ct);
        await PingAsync(two, 2, Second);
        await rig.Started.NextAsync(started => started.Source == RuntimeBrain.Source(two), ct);
        // Every save of the runtime publishes one BrainRuntimeChanged, so its log counts its saves.
        var before = (await rig.Brain.History<BrainRuntimeChanged>(rig.Runtime, ct)).Length;
        var ranBefore = (await rig.Facts<BehaviorRan>(ct)).Length;

        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(first, 1), new AcknowledgeReport(second, 1)]);
        var ran = new[] { await rig.Ran.NextAsync(ct: ct), await rig.Ran.NextAsync(ct: ct) };

        Assert.Equal(before + 1, (await rig.Brain.History<BrainRuntimeChanged>(rig.Runtime, ct)).Length);
        Assert.Equal(ranBefore + 2, (await rig.Facts<BehaviorRan>(ct)).Length);
        Assert.Equal([RuntimeBrain.Source(one), RuntimeBrain.Source(two)], ran.Select(fact => fact.Source).Order(StringComparer.Ordinal));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASignalPushedOutOfAFullBufferIsADroppedRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(maximumPending: 1, ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(started => started.Watermark == 1, ct);

        await PingAsync(pinger, 2, Second);
        var dropped = await rig.Ran.NextAsync(ct: ct);

        Assert.Equal((RunOutcome.Dropped, 1, 1L, 0L), (dropped.Outcome, dropped.Batches, dropped.Watermark, dropped.DurationMicroseconds));
        Assert.Equal(2, (await rig.Started.NextAsync(ct: ct)).Watermark);
        var view = Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors);
        Assert.Equal((0L, 0L, 1L), (view.Runs, view.Failures, view.Subscriptions.Single().Dropped));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ScriptCostCarriesTheSynapseAndApp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);

        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, CpuMicroseconds: 1_234_567)]);
        // A replayed acknowledgement is no run and costs nothing again.
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, CpuMicroseconds: 1_234_567)]);

        var cost = Assert.Single(rig.Meter.Calls, call => call.Kind == CostKind.Script);
        Assert.Equal((Behavior, Owner, null), (cost.Subject?.Synapse, cost.Subject?.App, cost.Subject?.Neuron));
        Assert.Equal(("cpu-ms", 1_234.567m, $"{subscription}:1"), (cost.Item, cost.Quantity, cost.CostId));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RunFactsComeFromASubscribableNeuron()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs", "synapses/runs.cs");
        var (reacting, delivered) = await RunWithAReactionAsync(rig, ct);

        var ran = Assert.Single(await rig.Facts<BehaviorRan>(ct));
        Assert.Equal(reacting, delivered.Subscription);
        var fact = JsonSerializer.Deserialize<BehaviorRan>(Assert.Single(delivered.Signals), RuntimeProtocol.Json)!;
        Assert.Equal((Behavior, RunOutcome.Succeeded), (fact.Behavior, fact.Outcome));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RunsOfARunFactReactionPublishNoRunFacts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs", "synapses/runs.cs");
        var (reacting, delivered) = await RunWithAReactionAsync(rig, ct);

        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(reacting, delivered.Watermark, CpuMicroseconds: 10, WallMicroseconds: 100)]);

        var views = (await rig.Runtime.Inspect(ct)).Behaviors;
        Assert.Equal((1L, 0L), (views.Single(view => view.Id == Behavior).Runs, views.Single(view => view.Id == Reaction).Runs));
        // A later fact of the other synapse is the barrier: whatever the runs neuron published before it is journaled.
        await PingAsync(rig.Brain.Get<IPinger>(Acc + "/one"), 2, null);
        await rig.Started.NextAsync(started => started.Watermark == 2, ct);
        var facts = await rig.AllFacts(ct);
        Assert.DoesNotContain(facts.OfType<BehaviorStarted>(), started => started.Behavior == Reaction);
        Assert.DoesNotContain(facts.OfType<BehaviorRan>(), ran => ran.Behavior == Reaction);
    }

    // Synapses stay inside a brain (06-08): a program cannot subscribe to another brain's runs at all, so two brains can no longer
    // feed each other run facts.
    [Trait("host", "own")]
    [Fact]
    public async Task AReactionToAnotherBrainsRunsIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs", "synapses/runs.cs");
        var theirs = rig.Brain.Get<IBrainRuns>(BrainRunsKeys.For(Other));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Edge.Subscribed(rig.Session, Reaction, RuntimeBrain.Source(theirs),
            DigitalBrain.Runtime.Orleans.Signalling.SignalLog.Alias(typeof(BehaviorRan))));
        Assert.Empty(Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors, view => view.Id == Reaction).Subscriptions);
    }

    // The guard is on the host's call path: a Record from anyone but the brain's own runtime publishes nothing.
    [Trait("host", "own")]
    [Fact]
    public async Task ARecordFromAnyoneButTheBrainsRuntimeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        var forged = new BehaviorRan(BrainRuntimeKeys.For(Acc), Owner, Owner + "/synapses/forged.cs", "forged", RuntimeBrain.Source(pinger),
            Pings.Pinged, 1, RunOutcome.Succeeded, 1, 0, 0, null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SiloNeurons.Get<IBrainRunsRecorder>(rig.Brain.Services, BrainRunsKeys.For(Acc)).Record([RunFact.Of(forged, First)], true));
        // The runtime's own next fact is the barrier: the runs neuron took the forged call before it.
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);

        var published = Assert.Single(await rig.AllFacts(ct));
        Assert.Equal(Behavior, Assert.IsType<BehaviorStarted>(published).Behavior);
    }

    // The runtime and the runs neuron are plain (06-08): a call leaves from the runtime's host grain, keyed by its neuron id.
    [Fact]
    public void RunsAreRecordedOnlyByTheirOwnBrainsRuntime()
    {
        static GrainId Host(string brain, string type, string key) => GrainId.Create("neuron.host", new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), type, key).ToString());
        var runs = Host(Acc, BrainRunsNeuron.GrainTypeName, "runs");

        Assert.True(BrainRunsGuard.Admits(Host(Acc, BrainRuntimeNeuron.GrainTypeName, BrainRuntimeKeys.Name), runs));
        Assert.False(BrainRunsGuard.Admits(null, runs));
        Assert.False(BrainRunsGuard.Admits(Host("other-brain", BrainRuntimeNeuron.GrainTypeName, BrainRuntimeKeys.Name), runs));
        Assert.False(BrainRunsGuard.Admits(Host(Acc, "apps.app2", "apps/x"), runs));
        Assert.False(BrainRunsGuard.Admits(runs, runs));
        Assert.False(BrainRunsGuard.Admits(GrainId.Create(BrainRuntimeNeuron.GrainTypeName, BrainRuntimeKeys.For(Acc)), runs));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RunFactsQueuedBehindACommitShareTheNextOrderedDurableAppend()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var assignment = Assert.Single(await rig.Edge.Assignments(rig.Session));
        await rig.Edge.Reported(rig.Session, new(Behavior, assignment.Hash, "Initialized", null, null, assignment.Revision));
        var pinger = rig.Brain.Get<IPinger>(Acc + "/queued-history");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await NextBatchAsync(rig, subscription, ct);
        await rig.Started.NextAsync(ct: ct);
        var storage = rig.Held;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.HeldRuns = release.Task;
        storage.RunsWriteEntered = entered;
        try
        {
            await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 1, WallMicroseconds: 100)]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await PingAsync(pinger, 2, Second);
            await NextBatchAsync(rig, subscription, ct);
            await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 2, WallMicroseconds: 200)]);
            Assert.False((await rig.Runtime.Inspect(ct)).IsIdle);
        }
        finally { release.TrySetResult(); }
        await TestWait.UntilAsync(rig.Runtime.Inspect, state => state.IsIdle, TimeSpan.FromSeconds(10), ct);
        // The second run's start and finish followed the held commit, in order, after the first run's.
        Assert.Equal([1L, 2L], (await rig.Facts<BehaviorStarted>(ct)).Select(started => started.Watermark));
        Assert.Equal([1L, 2L], (await rig.Facts<BehaviorRan>(ct)).Select(ran => ran.Watermark));
        Assert.Equal(2, (await rig.Runs.Read()).Recent.Length);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task GracefulRuntimeDeactivationWaitsForItsHistoryCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>(Acc + "/deactivation-history");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        var storage = rig.Held;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.HeldRuns = release.Task;
        storage.RunsWriteEntered = entered;
        Task deactivating;
        try
        {
            await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 1, WallMicroseconds: 100)]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            deactivating = rig.Brain.DeactivateAsync(rig.Runtime, ct);
            Assert.False(deactivating.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await deactivating.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(100, Assert.Single((await rig.Runs.Read()).Recent).DurationMicroseconds);
    }

    // Synapse a.cs reacts to the pinger; runs.cs reacts to a.cs's run facts. Returns the reaction's
    // subscription and the batch of run facts pushed to it.
    private static async Task<(string Reacting, SignalBatchEvent Delivered)> RunWithAReactionAsync(Rig rig, CancellationToken ct)
    {
        var pinger = rig.Brain.Get<IPinger>(Acc + "/one");
        var reacting = await rig.Edge.Subscribed(rig.Session, Reaction, RuntimeBrain.Source(rig.Runs), DigitalBrain.Runtime.Orleans.Signalling.SignalLog.Alias(typeof(BehaviorRan)));
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, WallMicroseconds: 50)]);
        await rig.Ran.NextAsync(ct: ct);
        return (reacting, await NextBatchAsync(rig, reacting, ct));
    }

    // The trace a caller's work runs under travels into the pinger, which publishes the signal under it.
    private static async Task PingAsync(IPinger pinger, int number, Trace? trace)
    {
        using var entered = trace is null ? null : TraceContext.Enter(trace);
        await pinger.Ping(number);
    }

    private static IEnumerable<SignalBatchEvent> Pushed(Channel<(string Kind, object Payload)> stream)
    {
        while (stream.Reader.TryRead(out var item))
        {
            if (item.Payload is SignalBatchEvent batch) { yield return batch; }
        }
    }

    // Waits on the stream itself, as the host does, for the next batch of one subscription.
    private static async Task<SignalBatchEvent> NextBatchAsync(Rig rig, string subscription, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(rig.Brain.Execution.AssertionTimeout);
        while (true)
        {
            var (_, payload) = await rig.Stream.Reader.ReadAsync(deadline.Token);
            if (payload is SignalBatchEvent batch && batch.Subscription == subscription) { return batch; }
        }
    }

    private static Task<Rig> StartAsync(CancellationToken ct, params string[] synapses) => StartAsync(new CSharpOptions().MaximumPending, ct, synapses);

    private static async Task<Rig> StartAsync(int maximumPending, CancellationToken ct, params string[] synapses)
    {
        var meter = new RecordingMeter();
        var held = new HeldRecords();
        var brain = await RuntimeBrain.Create(new FakeSessionHost())
            .WithOptions<CSharpModule, CSharpOptions>(options => options.MaximumPending = maximumPending)
            .ConfigureServices(services => services.AddSingleton<ICostMeter>(meter).AddSingleton<IIncomingGrainCallFilter>(held))
            .StartAsync(ct);
        try
        {
            var connected = await ConnectAsync(brain, Acc, Owner, ct, synapses);
            return new Rig(brain, connected.Session, connected.Stream, held, meter, connected.Started, connected.Ran);
        }
        catch
        {
            await brain.DisposeAsync();
            throw;
        }
    }

    // A brain's program assigned and its session connected with an open stream, so every pushed batch reaches the
    // host and starts a run; the probes watch that brain's runs neuron.
    private static async Task<Connected> ConnectAsync(ITestBrain brain, string scope, string owner, CancellationToken ct, params string[] synapses)
    {
        await RuntimeBrain.Programs(brain, scope).Assign(owner, synapses.ToDictionary(path => path, _ => RuntimeBrain.Idle, StringComparer.Ordinal), []);
        var session = (await RuntimeBrain.Runtime(brain, scope).Inspect(ct)).SessionId!;
        await RuntimeBrain.Edge(brain, scope).Connected(session, RuntimeBrain.Fingerprint(brain));
        var stream = brain.Services.GetRequiredService<SessionStreams>().Open(session);
        var runs = brain.Get<IBrainRuns>(BrainRunsKeys.For(scope));
        return new Connected(session, stream, await brain.Observe<BehaviorStarted>(runs, ct), await brain.Observe<BehaviorRan>(runs, ct));
    }

    private sealed record Connected(string Session, Channel<(string Kind, object Payload)> Stream, PlainSignalProbe<BehaviorStarted> Started, PlainSignalProbe<BehaviorRan> Ran);

    private sealed record Rig(ITestBrain Brain, string Session, Channel<(string Kind, object Payload)> Stream, HeldRecords Held, RecordingMeter Meter,
        PlainSignalProbe<BehaviorStarted> Started, PlainSignalProbe<BehaviorRan> Ran) : IAsyncDisposable
    {
        // What the runs neuron published, read from the brain's log.
        public Task<T[]> Facts<T>(CancellationToken ct) where T : DigitalBrain.Signal => Brain.History<T>(Runs, ct);

        public async Task<DigitalBrain.Signal[]> AllFacts(CancellationToken ct)
            => [.. await Facts<BehaviorStarted>(ct), .. await Facts<BehaviorRan>(ct)];

        public IBrainRuntimeEdge Edge => RuntimeBrain.Edge(Brain, Acc);
        public IBrainRuntime Runtime => RuntimeBrain.Runtime(Brain, Acc);
        public IBrainRuns Runs => Brain.Get<IBrainRuns>(BrainRunsKeys.For(Acc));

        // The brain owns the probes it handed out and disposes them with itself.
        public ValueTask DisposeAsync() => Brain.DisposeAsync();
    }

    // Where every publish of the brain becomes durable: the facts count each append, and the host's own journal
    // keeps and serves them, so readers and the storage that journals state-attached facts share one sequence.
    // Holds the runtime's Record on the runs neuron's host until released, the way a slow commit holds it.
    private sealed class HeldRecords : IIncomingGrainCallFilter
    {
        private static readonly string Recorder = DigitalBrain.Invocation.NeuronProxy.ContractName(typeof(IBrainRunsRecorder));
        public Task? HeldRuns { get; set; }
        public TaskCompletionSource? RunsWriteEntered { get; set; }

        public async Task Invoke(IIncomingGrainCallContext context)
        {
            if (HeldRuns is { } held && context.Request.GetArgumentCount() > 0
                && context.Request.GetArgument(0) is DigitalBrain.Invocation.RuntimeInvocation { Method: nameof(IBrainRunsRecorder.Record) } call
                && call.Contract == Recorder)
            {
                RunsWriteEntered?.TrySetResult();
                await held;
            }
            await context.Invoke();
        }
    }

    private sealed record Recorded(CostKind Kind, string Item, decimal Quantity, string CostId, string? TraceId, CostSubject? Subject);

    private sealed class RecordingMeter : ICostMeter
    {
        public ValueTask<ExternalCallHandle> BeginExternalCallAsync(ExternalCallRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask MarkExternalCallDispatchStartedAsync(ExternalCallHandle handle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ExternalCallCompletion> CompleteExternalCallAsync(ExternalCallHandle handle, ExternalCallOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ConcurrentQueue<Recorded> Calls { get; } = new();
        public Task? Held { get; set; }

        public async ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
            string costId, CancellationToken cancellationToken, CostSubject? subject = null)
        {
            Calls.Enqueue(new Recorded(kind, item, quantity, costId, TraceContext.Current?.TraceId, subject));
            if (Held is { } held) { await held.WaitAsync(cancellationToken); }
            return new Cost { TraceId = TraceContext.Current?.TraceId ?? "", CostId = costId, Kind = kind, Item = item, Quantity = quantity, Unit = unit, Micros = 0 };
        }
    }
}
