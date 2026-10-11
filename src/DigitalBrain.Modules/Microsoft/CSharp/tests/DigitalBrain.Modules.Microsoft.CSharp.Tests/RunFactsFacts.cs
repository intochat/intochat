using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using DigitalBrain.Microsoft.CSharp.Sessions;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

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

    // The run's span sits under the span the pushed batch carried, in the batch's own trace, so a trace viewer
    // finds its parent.
    [Fact]
    public async Task ARunsSpanIsAChildOfItsBatchsSpan()
    {
        var ct = TestContext.Current.CancellationToken;
        var spans = new System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "DigitalBrain.Microsoft.CSharp.Runtime",
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
        const string Rewritten = "await using var brain = await DigitalBrainClient.ConnectAsync(args);\nConsole.WriteLine(\"rewritten\");\nawait Task.Delay(Timeout.Infinite, brain.Stopping);\n";

        await programs.Assign(Owner, new(StringComparer.Ordinal) { ["synapses/a.cs"] = RuntimeBrain.Idle, ["synapses/b.cs"] = Rewritten }, []);
        var kept = (await rig.Runtime.Inspect(ct)).Behaviors.Single(view => view.Id == Behavior);
        await programs.Assign(Owner, new(StringComparer.Ordinal) { ["synapses/a.cs"] = Rewritten, ["synapses/b.cs"] = Rewritten }, []);
        var rewritten = (await rig.Runtime.Inspect(ct)).Behaviors.Single(view => view.Id == Behavior);

        Assert.Equal((1L, 0L, 50L), (kept.Runs, kept.Failures, kept.LastDurationMicroseconds));
        Assert.NotEqual(kept.Hash, rewritten.Hash);
        Assert.Equal((0L, 0L, 0L), (rewritten.Runs, rewritten.Failures, rewritten.LastDurationMicroseconds));
    }

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
        var (runtimeAppends, runsAppends) = (rig.Journal.Of(runtime).Length, rig.Journal.Of(runs).Length);

        await rig.Edge.AcknowledgeMany(rig.Session, [new AcknowledgeReport(first, 1), new AcknowledgeReport(second, 1)]);
        var ran = new[] { await rig.Ran.NextAsync(ct: ct), await rig.Ran.NextAsync(ct: ct) };

        Assert.Equal(runtimeAppends + 1, rig.Journal.Of(runtime).Length);
        var appended = Assert.Single(rig.Journal.Of(runs).Skip(runsAppends));
        Assert.Equal(2, appended.Signals.OfType<BehaviorRan>().Count());
        Assert.Equal([one.GetGrainId().ToString(), two.GetGrainId().ToString()], ran.Select(fact => fact.Source).Order(StringComparer.Ordinal));
        Assert.Equal([First.TraceId, Second.TraceId], ran.OrderBy(fact => fact.Source, StringComparer.Ordinal).Select(fact => fact.Trace?.TraceId));
    }

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
    [Fact]
    public async Task ARecordFromAnyoneButTheBrainsRuntimeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await StartAsync(ct, "synapses/a.cs");
        var pinger = rig.Brain.Get<IPinger>("one");
        await rig.Edge.Subscribed(rig.Session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        var forged = new BehaviorRan(BrainRuntimeKeys.For(Acc), Owner, Owner + "/synapses/forged.cs", "forged", pinger.GetGrainId().ToString(),
            nameof(Pinged), 1, RunOutcome.Succeeded, 1, 0, 0, null);

        await rig.Brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IBrainRunsRecorder>(BrainRunsKeys.For(Acc)).Record([new RunFact(forged, First)]);
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
            .ConfigureServices(services => services.AddSingleton<ISignalJournal>(journal).AddSingleton<ICostMeter>(meter)
                .AddSingleton<IActivationHandler<BrainRuntimeNeuron>>(activations))
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

    // Where every publish of the brain becomes durable: the facts count each append, as the part log would.
    private sealed class RunJournal : ISignalJournal
    {
        private readonly ConcurrentQueue<(GrainId Publisher, Signal[] Signals)> _appends = new();

        public (GrainId Publisher, Signal[] Signals)[] Of(GrainId publisher) => [.. _appends.Where(append => append.Publisher == publisher)];

        public T[] Facts<T>(GrainId publisher) where T : Signal => [.. Of(publisher).SelectMany(append => append.Signals).OfType<T>()];

        public Task Append(GrainId publisher, Signal[] signals)
        {
            _appends.Enqueue((publisher, signals));
            return Task.CompletedTask;
        }

        public Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit) => Task.FromResult<JournaledSignal[]>([]);

        // Nothing to read back: a host without a part log, so live signals are what arrives.
        public Task<PartSignals?> SignalsOf(GrainId publisher, long since) => Task.FromResult<PartSignals?>(null);
    }

    private sealed record Recorded(CostKind Kind, string Item, decimal Quantity, string CostId, string? TraceId, CostSubject? Subject);

    private sealed class RecordingMeter : ICostMeter
    {
        public ConcurrentQueue<Recorded> Calls { get; } = new();

        public ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
            string costId, CancellationToken cancellationToken, CostSubject? subject = null)
        {
            Calls.Enqueue(new Recorded(kind, item, quantity, costId, TraceContext.Current?.TraceId, subject));
            return ValueTask.FromResult(new Cost { TraceId = TraceContext.Current?.TraceId ?? "", CostId = costId, Kind = kind, Item = item, Quantity = quantity, Unit = unit, Micros = 0 });
        }
    }
}
