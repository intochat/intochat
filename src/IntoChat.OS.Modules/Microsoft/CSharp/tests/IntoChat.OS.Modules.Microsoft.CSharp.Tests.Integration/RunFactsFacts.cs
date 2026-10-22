using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using IntoChat.OS.Client.Runtime.V1;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Microsoft.CSharp;
using IntoChat.OS.Microsoft.CSharp.Runtime;
using IntoChat.OS.Microsoft.CSharp.Runtime.Signals;
using IntoChat.OS.Microsoft.CSharp.Sessions;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// A synapse's runs are facts the brain's runs neuron publishes: a batch on a live stream starts a run, an
// acknowledgement that moves the subscription finishes it, an exit with batches in flight fails it.
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
        var pinger = rig.Brain.Get<IPinger>("history-invalidation");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await rig.Brain.Observe<BrainRunsChanged>(rig.Runs, ct);
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        Assert.Empty(rig.Journal.Facts<BrainRunsChanged>(rig.Runs.GetGrainId()));
        await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 1, WallMicroseconds: 1_500)]);
        await rig.Ran.NextAsync(ct: ct);
        Assert.Single(rig.Journal.Facts<BrainRunsChanged>(rig.Runs.GetGrainId()));
        var change = await changes.NextAsync(ct: ct);
        Assert.Equal((await rig.Runs.Read()).Revision, change.Revision);
        Assert.Equal(First.TraceId, change.Trace?.TraceId);
        await PingAsync(pinger, 2, Second);
        await rig.Started.NextAsync(ct: ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 2, Skipped: true)]);
        await rig.Ran.NextAsync(ct: ct);
        // Both completed/skipped facts remain observable, but skipped work is not history.
        await TestWait.UntilAsync(_ => Task.FromResult(rig.Journal.Facts<BehaviorRan>(rig.Runs.GetGrainId()).Count()), count => count == 2, TimeSpan.FromSeconds(10), ct);
        Assert.Single(rig.Journal.Facts<BrainRunsChanged>(rig.Runs.GetGrainId()));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RunSnapshotsSurviveReactivationAndReplayDoesNotChangeStatistics()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>("snapshot");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 1, WallMicroseconds: 1_500)]);
        await rig.Ran.NextAsync(ct: ct);
        var before = await rig.Runs.Read();
        Assert.Equal(First.TraceId, Assert.Single(before.Recent).TraceId);
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
        var pinger = rig.Brain.Get<IPinger>("idle-pricing");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
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
        var pinger = rig.Brain.Get<IPinger>("idle-history");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        var storage = rig.Brain.Services.GetRequiredService<RecordingWrites>();
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
        var pinger = rig.Brain.Get<IPinger>("one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));

        await PingAsync(pinger, 1, First);
        var started = await rig.Started.NextAsync(ct: ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, CpuMicroseconds: 300, WallMicroseconds: 1_500)]);
        var ran = await rig.Ran.NextAsync(ct: ct);

        var view = Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors);
        Assert.Equal((RunOutcome.Succeeded, 1, 1_500L, 300L, 1L, null), (ran.Outcome, ran.Batches, ran.DurationMicroseconds, ran.CpuMicroseconds, ran.Watermark, ran.Error));
        Assert.Equal((BrainRuntimeKeys.For(Acc), Owner, Behavior, view.Hash, pinger.GetGrainId().ToString(), nameof(Pinged)),
            (ran.RuntimeId, ran.Owner, ran.Behavior, ran.Hash, ran.Source, ran.SignalType));
        Assert.Equal(First.TraceId, ran.Trace?.TraceId);
        Assert.Equal((Behavior, 1L, First.TraceId), (started.Behavior, started.Watermark, started.Trace?.TraceId));
        Assert.Single(rig.Journal.Facts<BehaviorRan>(rig.Runs.GetGrainId()));
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
        var pinger = rig.Brain.Get<IPinger>("one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));

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
        var pinger = rig.Brain.Get<IPinger>("one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));

        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        // A handled and a skipped report for the same subscription in one call: the subscription ran.
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, WallMicroseconds: 70), new AcknowledgeReport(subscription, 1, Skipped: true)]);
        var ran = await rig.Ran.NextAsync(ct: ct);
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, WallMicroseconds: 70)]);

        var view = Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors);
        Assert.Equal(RunOutcome.Succeeded, ran.Outcome);
        Assert.Equal(1L, view.Runs);
        Assert.Single(rig.Journal.Facts<BehaviorRan>(rig.Runs.GetGrainId()));
    }

    // The run's span sits under the span the pushed batch carried, in the batch's own trace, so a trace viewer
    // finds its parent.
    [Trait("host", "own")]
    [Fact]
    public async Task ARunsSpanIsAChildOfItsBatchsSpan()
    {
        var ct = TestContext.Current.CancellationToken;
        var spans = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "IntoChat.OS.Microsoft.CSharp.Runtime",
            Sample = (ref _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>("one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);

        var batch = Assert.Single(await rig.Edge.PendingBatches(rig.Session));
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, CpuMicroseconds: 300, WallMicroseconds: 1_500)]);

        var parent = System.Diagnostics.ActivityContext.Parse(batch.TraceParent!, null);
        Assert.Equal(First.TraceId, parent.TraceId.ToHexString());
        Assert.Contains(spans, span => span.OperationName == "runtime.batch" && span.TraceId == parent.TraceId && span.ParentSpanId == parent.SpanId);
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
        var pinger = rig.Brain.Get<IPinger>("untraced");
        await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
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
        var pinger = rig.Brain.Get<IPinger>("one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);

        // A reloaded behavior subscribes again and is pushed the same batch; a reconnecting stream pulls it.
        await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        Assert.Equal(1, Assert.Single(await rig.Edge.PendingBatches(rig.Session)).Watermark);
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1)]);
        await rig.Ran.NextAsync(ct: ct);

        Assert.Equal(2, Pushed(rig.Stream).Count(batch => batch.Subscription == subscription && batch.Watermark == 1));
        Assert.Single(rig.Journal.Facts<BehaviorStarted>(rig.Runs.GetGrainId()));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnExitWithAnUnackedBatchIsAFailedRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>("one");
        await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        var hash = Assert.Single((await rig.Runtime.Inspect(ct)).Behaviors).Hash;

        await rig.Edge.Reported(rig.Session, new BehaviorStatusReport(Behavior, hash, "Exited", 1, "boom"));
        var failed = await rig.Ran.NextAsync(ct: ct);

        Assert.Equal((RunOutcome.Failed, 1, 1L, "boom", 0L), (failed.Outcome, failed.Batches, failed.Watermark, failed.Error, failed.DurationMicroseconds));
        Assert.Equal(First.TraceId, failed.Trace?.TraceId);
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
        var pinger = rig.Brain.Get<IPinger>("one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
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
        var pinger = rig.Brain.Get<IPinger>("one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(started => started.Watermark == 1, ct);
        await PingAsync(pinger, 2, Second);
        await rig.Started.NextAsync(started => started.Watermark == 2, ct);
        Assert.Equal(2, rig.Activations[Acc].TracedBatches);

        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, 100, 1_000), new AcknowledgeReport(subscription, 2, 20, 300)]);
        var ran = await rig.Ran.NextAsync(ct: ct);

        Assert.Equal((2, 1_300L, 120L, 2L), (ran.Batches, ran.DurationMicroseconds, ran.CpuMicroseconds, ran.Watermark));
        // Settling through watermark 2 let go of both batches' traces, not just the one acknowledged last.
        Assert.Equal(0, rig.Activations[Acc].TracedBatches);
        // The batch at watermark 2 carried signal 1 again, and a batch's trace is its first signal's.
        Assert.Equal(First.TraceId, ran.Trace?.TraceId);
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
        var one = rig.Brain.Get<IPinger>("one");
        var two = rig.Brain.Get<IPinger>("two");
        var first = await rig.Edge.Subscribed(rig.Session, Behavior, one.GetGrainId().ToString(), nameof(Pinged));
        var second = await rig.Edge.Subscribed(rig.Session, Behavior, two.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(one, 1, First);
        await rig.Started.NextAsync(started => started.Source == one.GetGrainId().ToString(), ct);
        await PingAsync(two, 2, Second);
        await rig.Started.NextAsync(started => started.Source == two.GetGrainId().ToString(), ct);
        var runtime = rig.Runtime.GetGrainId();
        var runs = rig.Runs.GetGrainId();
        var runtimeWrites = rig.Brain.Services.GetRequiredService<RecordingWrites>();
        var before = runtimeWrites.Count(runtime);
        var runsAppends = rig.Journal.Of(runs).Length;

        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(first, 1), new AcknowledgeReport(second, 1)]);
        var ran = new[] { await rig.Ran.NextAsync(ct: ct), await rig.Ran.NextAsync(ct: ct) };

        Assert.Equal(before + 1, runtimeWrites.Count(runtime));
        var appended = Assert.Single(rig.Journal.Of(runs).Skip(runsAppends));
        Assert.Equal(2, appended.Signals.OfType<BehaviorRan>().Count());
        Assert.Equal([one.GetGrainId().ToString(), two.GetGrainId().ToString()], ran.Select(fact => fact.Source).Order(StringComparer.Ordinal));
        Assert.Equal([First.TraceId, Second.TraceId], ran.OrderBy(fact => fact.Source, StringComparer.Ordinal).Select(fact => fact.Trace?.TraceId));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASignalPushedOutOfAFullBufferIsADroppedRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(maximumPending: 1, ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>("one");
        await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
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
        var pinger = rig.Brain.Get<IPinger>("one");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);

        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, CpuMicroseconds: 1_234_567)]);
        // A replayed acknowledgement is no run and costs nothing again.
        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(subscription, 1, CpuMicroseconds: 1_234_567)]);

        var cost = Assert.Single(rig.Meter.Calls, call => call.Kind == CostKind.Script);
        Assert.Equal((Behavior, Owner, null), (cost.Subject?.Synapse, cost.Subject?.App, cost.Subject?.Neuron));
        Assert.Equal(("cpu-ms", 1_234.567m, $"{subscription}:1", First.TraceId), (cost.Item, cost.Quantity, cost.CostId, cost.TraceId));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RunFactsComeFromASubscribableNeuron()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs", "synapses/runs.cs");
        var runs = rig.Runs.GetGrainId().ToString();

        var (reacting, delivered) = await RunWithAReactionAsync(rig, ct);

        var ran = Assert.Single(rig.Journal.Facts<BehaviorRan>(rig.Runs.GetGrainId()));
        Assert.Equal(runs, ran.Publisher.Name);
        Assert.Equal(reacting, delivered.Subscription);
        var fact = JsonSerializer.Deserialize<BehaviorRan>(Assert.Single(delivered.Signals), RuntimeProtocol.Json)!;
        Assert.Equal((Behavior, RunOutcome.Succeeded, First.TraceId), (fact.Behavior, fact.Outcome, fact.Trace?.TraceId));
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
        await PingAsync(rig.Brain.Get<IPinger>("one"), 2, null);
        await rig.Started.NextAsync(started => started.Watermark == 2, ct);
        var facts = rig.Journal.Facts<Signal>(rig.Runs.GetGrainId());
        Assert.DoesNotContain(facts.OfType<BehaviorStarted>(), started => started.Behavior == Reaction);
        Assert.DoesNotContain(facts.OfType<BehaviorRan>(), ran => ran.Behavior == Reaction);
    }

    // Any brain's runs neuron is a source of run facts: a reaction to another brain's runs publishes none either,
    // or two brains reacting to each other's runs would feed each other without end.
    [Trait("host", "own")]
    [Fact]
    public async Task RunsOfAReactionToAnotherBrainsRunsPublishNoRunFacts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs", "synapses/runs.cs");
        var other = await ConnectAsync(rig.Brain, Other, OtherOwner, ct, "synapses/a.cs");
        var otherEdge = RuntimeBrain.Edge(rig.Brain, Other);
        var reacting = await rig.Edge.Subscribed(rig.Session, Reaction, rig.Brain.Get<IBrainRuns>(BrainRunsKeys.For(Other)).GetGrainId().ToString(), nameof(BehaviorRan));
        var theirs = rig.Brain.Get<IPinger>("theirs");
        var subscription = await otherEdge.Subscribed(other.Session, OtherBehavior, theirs.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(theirs, 1, null);
        await other.Started.NextAsync(ct: ct);
        await otherEdge.AcknowledgeMany(other.Session, [new AcknowledgeReport(subscription, 1)]);
        var delivered = await NextBatchAsync(rig, reacting, ct);

        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(reacting, delivered.Watermark, CpuMicroseconds: 10, WallMicroseconds: 100)]);

        Assert.Equal(OtherBehavior, JsonSerializer.Deserialize<BehaviorRan>(Assert.Single(delivered.Signals), RuntimeProtocol.Json)!.Behavior);
        Assert.Equal(0L, (await rig.Runtime.Inspect(ct)).Behaviors.Single(view => view.Id == Reaction).Runs);
        // A later fact of the other synapse is the barrier: whatever the runs neuron published before it is journaled.
        var ours = rig.Brain.Get<IPinger>("ours");
        await rig.Edge.Subscribed(rig.Session, Behavior, ours.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(ours, 2, First);
        await rig.Started.NextAsync(started => started.Behavior == Behavior, ct);
        var published = Assert.Single(rig.Journal.Facts<Signal>(rig.Runs.GetGrainId()));
        Assert.Equal(Behavior, Assert.IsType<BehaviorStarted>(published).Behavior);
    }

    // The guard is on the host's call path: a Record from anyone but the brain's own runtime publishes nothing.
    [Trait("host", "own")]
    [Fact]
    public async Task ARecordFromAnyoneButTheBrainsRuntimeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>("one");
        await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        var forged = new BehaviorRan(BrainRuntimeKeys.For(Acc), Owner, Owner + "/synapses/forged.cs", "forged", pinger.GetGrainId().ToString(),
            nameof(Pinged), 1, RunOutcome.Succeeded, 1, 0, 0, null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Brain.Services.GetRequiredService<IGrainFactory>()
            .GetGrain<IBrainRunsRecorder>(BrainRunsKeys.For(Acc)).Record([new RunFact(forged, First)], true));
        // The runtime's own next fact is the barrier: the runs neuron took the forged call before it.
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);

        var published = Assert.Single(rig.Journal.Facts<Signal>(rig.Runs.GetGrainId()));
        Assert.Equal(Behavior, Assert.IsType<BehaviorStarted>(published).Behavior);
    }

    [Fact]
    public void RunsAreRecordedOnlyByTheirOwnBrainsRuntime()
    {
        var runs = GrainId.Create(BrainRunsNeuron.GrainTypeName, BrainRunsKeys.For(Acc));

        Assert.True(BrainRunsGuard.Admits(GrainId.Create(BrainRuntimeNeuron.GrainTypeName, BrainRuntimeKeys.For(Acc)), runs));
        Assert.False(BrainRunsGuard.Admits(null, runs));
        Assert.False(BrainRunsGuard.Admits(GrainId.Create(BrainRuntimeNeuron.GrainTypeName, BrainRuntimeKeys.For("other-brain")), runs));
        Assert.False(BrainRunsGuard.Admits(GrainId.Create("apps.app", Owner), runs));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RunFactsQueuedBehindACommitShareTheNextOrderedDurableAppend()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var assignment = Assert.Single(await rig.Edge.Assignments(rig.Session));
        await rig.Edge.Reported(rig.Session, new(Behavior, assignment.Hash, "Initialized", null, null, assignment.Revision));
        var pinger = rig.Brain.Get<IPinger>("queued-history");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(pinger, 1, First);
        await NextBatchAsync(rig, subscription, ct);
        await rig.Started.NextAsync(ct: ct);
        var storage = rig.Brain.Services.GetRequiredService<RecordingWrites>();
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
        var appends = rig.Journal.Of(rig.Runs.GetGrainId());
        Assert.Equal(3, appends.Length);
        Assert.Collection(appends[^1].Signals.Where(signal => signal is BehaviorStarted or BehaviorRan),
            signal => Assert.Equal((2L, Second.TraceId), (Assert.IsType<BehaviorStarted>(signal).Watermark, signal.Trace?.TraceId)),
            signal => Assert.Equal((2L, Second.TraceId), (Assert.IsType<BehaviorRan>(signal).Watermark, signal.Trace?.TraceId)));
        Assert.Equal([First.TraceId, Second.TraceId], (await rig.Runs.Read()).Recent.Select(run => run.TraceId));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task GracefulRuntimeDeactivationWaitsForItsHistoryCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>("deactivation-history");
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await PingAsync(pinger, 1, First);
        await rig.Started.NextAsync(ct: ct);
        var storage = rig.Brain.Services.GetRequiredService<RecordingWrites>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.HeldRuns = release.Task;
        storage.RunsWriteEntered = entered;
        Task deactivating;
        try
        {
            await rig.Edge.AcknowledgeMany(rig.Session, [new(subscription, 1, WallMicroseconds: 100)]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            deactivating = rig.Activations[Acc].OnDeactivateAsync(
                new DeactivationReason(DeactivationReasonCode.ApplicationRequested, "Retiring the runtime"), ct);
            Assert.False(deactivating.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await deactivating.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(First.TraceId, Assert.Single((await rig.Runs.Read()).Recent).TraceId);
    }

    // Synapse a.cs reacts to the pinger; runs.cs reacts to a.cs's run facts. Returns the reaction's
    // subscription and the batch of run facts pushed to it.
    private static async Task<(string Reacting, SignalBatchEvent Delivered)> RunWithAReactionAsync(Rig rig, CancellationToken ct)
    {
        var pinger = rig.Brain.Get<IPinger>("one");
        var reacting = await rig.Edge.Subscribed(rig.Session, Reaction, rig.Runs.GetGrainId().ToString(), nameof(BehaviorRan));
        var subscription = await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
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
        var journal = new RunJournal();
        var meter = new RecordingMeter();
        var activations = new RuntimeActivations();
        var brain = await RuntimeBrain.Create(new FakeSessionHost())
            .WithOptions<CSharpModule, CSharpOptions>(options => options.MaximumPending = maximumPending)
            .ConfigureServices(services =>
            {
                var journaling = services.Last(descriptor => descriptor.ServiceType == typeof(ISignalJournal));
                services.Remove(journaling);
                services.AddSingleton<ISignalJournal>(provider => journal.Over(journaling.ImplementationInstance as ISignalJournal
                    ?? (ISignalJournal?)journaling.ImplementationFactory?.Invoke(provider)
                    ?? (ISignalJournal)ActivatorUtilities.CreateInstance(provider, journaling.ImplementationType!)));
                services.AddSingleton<ICostMeter>(meter)
                    .AddSingleton<IActivationHandler<BrainRuntimeNeuron>>(activations);
                var storage = services.Last(descriptor => descriptor.ServiceType == typeof(IGrainStorage)
                    && Equals(descriptor.ServiceKey, IntoChatOSNames.ClusterGrainStorage));
                services.Remove(storage);
                services.AddSingleton(provider => new RecordingWrites((IGrainStorage)storage.KeyedImplementationFactory!(provider, IntoChatOSNames.ClusterGrainStorage), journal));
                services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.ClusterGrainStorage, (provider, _) => provider.GetRequiredService<RecordingWrites>());
            })
            .StartAsync(ct);
        try
        {
            var connected = await ConnectAsync(brain, Acc, Owner, ct, synapses);
            return new Rig(brain, connected.Session, connected.Stream, journal, meter, activations, connected.Started, connected.Ran);
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

    private sealed record Connected(string Session, Channel<(string Kind, object Payload)> Stream, SignalProbe<BehaviorStarted> Started, SignalProbe<BehaviorRan> Ran);

    // Each brain runtime's live activation, for what only its memory holds.
    private sealed class RuntimeActivations : IActivationHandler<BrainRuntimeNeuron>
    {
        private readonly ConcurrentDictionary<string, BrainRuntimeNeuron> _current = new(StringComparer.Ordinal);

        public BrainRuntimeNeuron this[string scope] => _current[BrainRuntimeKeys.For(scope)];

        public Task Activated(BrainRuntimeNeuron neuron, CancellationToken cancellationToken)
        {
            _current[neuron.GetPrimaryKeyString()] = neuron;
            return Task.CompletedTask;
        }
    }

    private sealed record Rig(ITestBrain Brain, string Session, Channel<(string Kind, object Payload)> Stream, RunJournal Journal, RecordingMeter Meter,
        RuntimeActivations Activations, SignalProbe<BehaviorStarted> Started, SignalProbe<BehaviorRan> Ran) : IAsyncDisposable
    {
        public IBrainRuntimeEdge Edge => RuntimeBrain.Edge(Brain, Acc);
        public IBrainRuntime Runtime => RuntimeBrain.Runtime(Brain, Acc);
        public IBrainRuns Runs => Brain.Get<IBrainRuns>(BrainRunsKeys.For(Acc));

        // The brain owns the probes it handed out and disposes them with itself.
        public ValueTask DisposeAsync() => Brain.DisposeAsync();
    }

    // Where every publish of the brain becomes durable: the facts count each append, and the host's own journal
    // keeps and serves them, so readers and the storage that journals state-attached facts share one sequence.
    private sealed class RunJournal : ISignalJournal
    {
        private readonly ConcurrentQueue<(GrainId Publisher, Signal[] Signals)> _appends = new();
        private ISignalJournal _inner = null!;

        public RunJournal Over(ISignalJournal inner)
        {
            _inner = inner;
            return this;
        }

        public (GrainId Publisher, Signal[] Signals)[] Of(GrainId publisher) => [.. _appends.Where(append => append.Publisher == publisher)];

        public T[] Facts<T>(GrainId publisher) where T : Signal => [.. Of(publisher).SelectMany(append => append.Signals).OfType<T>()];

        // Facts the storage write journalled itself: counted here, already kept by the host's journal.
        public void Record(GrainId publisher, Signal[] signals) => _appends.Enqueue((publisher, signals));

        public Task<long> Append(GrainId publisher, Signal[] signals)
        {
            Record(publisher, signals);
            return _inner.Append(publisher, signals);
        }

        public Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit) => _inner.ReadSince(brainId, since, limit);

        public Task<PartSignals?> SignalsOf(GrainId publisher, long since) => _inner.SignalsOf(publisher, since);
    }

    private sealed class RecordingWrites(IGrainStorage inner, RunJournal journal) : IGrainStorage
    {
        private readonly ConcurrentDictionary<GrainId, int> writes = new();
        public Task? HeldRuns { get; set; }
        public TaskCompletionSource? RunsWriteEntered { get; set; }
        public int Count(GrainId id) => writes.GetValueOrDefault(id);
        public Task ReadStateAsync<T>(string name, GrainId id, IGrainState<T> state) => inner.ReadStateAsync(name, id, state);
        public async Task WriteStateAsync<T>(string name, GrainId id, IGrainState<T> state)
        {
            if (id.Type.ToString() == BrainRunsNeuron.GrainTypeName && HeldRuns is { } held)
            {
                RunsWriteEntered?.TrySetResult();
                await held;
            }
            // The durable journal now also receives facts attached to a read-model state write.
            // Observe that commit without consuming the attachment before the real storage does.
            var pending = SignalJournal.TakePending(id);
            SignalJournal.Restore(id, pending);
            await inner.WriteStateAsync(name, id, state);
            var remaining = SignalJournal.TakePending(id);
            SignalJournal.Restore(id, remaining);
            if (pending.Length > 0 && remaining.Length == 0) { journal.Record(id, pending); }
            writes.AddOrUpdate(id, 1, (_, count) => count + 1);
        }
        public Task ClearStateAsync<T>(string name, GrainId id, IGrainState<T> state) => inner.ClearStateAsync(name, id, state);
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
