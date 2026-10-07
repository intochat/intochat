using DigitalBrain;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Testing;
using DigitalBrain.Microsoft.CSharp.Edge;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Files.Signals;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class CSharpSubscriptionFacts
{
    [Fact]
    public async Task OppositePublicationOrdersAcrossSubscriptionsHaveIdenticalWakeReplayPayloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);

        async Task<string[]> Replay(bool researchFirst)
        {
            var scope = "workspace/stop-order/" + researchFirst;
            var research = brain.Get<IPinger>(scope + "/research");
            var stop = brain.Get<IPinger>(scope + "/stop");
            var file = await Subscribed(brain, sandbox, scope, research, ct);
            var edge = file.AsReference<ICSharpFileEdge>();
            await edge.Subscribed(sandbox.LatestRunId, stop.GetGrainId().ToString(), nameof(Pinged));
            sandbox.ExitLatest(0);

            var first = researchFirst ? research : stop;
            var second = researchFirst ? stop : research;
            await first.Ping(1);
            await Eventually(async () => (await file.Inspect(ct)).Subscriptions.Single(s => s.Neuron == first.GetGrainId().ToString()).Pending == 1, ct);
            await second.Ping(1);
            await Eventually(async () => (await file.Inspect(ct)).Subscriptions.All(s => s.Pending == 1), ct);

            // A resumed Stop loop can drain first regardless of publication order.
            var stopped = Assert.Single((await edge.DrainPending(sandbox.LatestRunId, stop.GetGrainId().ToString(), nameof(Pinged))).Signals);
            var requested = Assert.Single((await edge.DrainPending(sandbox.LatestRunId, research.GetGrainId().ToString(), nameof(Pinged))).Signals);
            return [stopped, requested];
        }

        var staleResearch = await Replay(researchFirst: true);
        var freshResearch = await Replay(researchFirst: false);
        Assert.Equal([1, 1], staleResearch.Select(SignalJson.Number));
        Assert.Equal(staleResearch.Select(SignalJson.Number), freshResearch.Select(SignalJson.Number));
    }

    [Fact]
    public async Task SwappingTheSourceKeepsSubscriptionsAndTheirBufferedSignals()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/swapping", pinger, ct);
        var edge = file.AsReference<ICSharpFileEdge>();
        await pinger.Ping(1);
        await Eventually(async () => (await file.Inspect(ct)).Subscriptions.Single().Pending == 1, ct);

        // An edit is not a fresh intent: the new source resumes the old one's subscriptions.
        await file.Swap("Console.WriteLine(2);", ct);

        var swapped = await file.Inspect(ct);
        Assert.Equal("Console.WriteLine(2);", swapped.Source);
        Assert.Equal(1, Assert.Single(swapped.Subscriptions).Pending);
        // Signals that arrive after the swap keep buffering on the surviving subscription.
        await pinger.Ping(2);
        await Eventually(async () => (await file.Inspect(ct)).Subscriptions.Single().Pending == 2, ct);
        var drained = await edge.DrainPending(sandbox.LatestRunId, pinger.GetGrainId().ToString(), nameof(Pinged));
        Assert.Equal(2, drained.Signals.Length);
    }

    [Fact]
    public async Task ASubscriptionIsRecordedOnceAndBuffersOnlyMatchingSignals()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/buffering", pinger, ct);
        var edge = file.AsReference<ICSharpFileEdge>();

        // Registering the same subscription again is a no-op.
        await edge.Subscribed(sandbox.LatestRunId, pinger.GetGrainId().ToString(), nameof(Pinged));
        await pinger.Ping(7);

        await Eventually(async () => (await edge.DrainPending(sandbox.LatestRunId, pinger.GetGrainId().ToString(), nameof(Pinged))) is { Signals.Length: > 0 }, ct);
        Assert.Single((await file.Inspect(ct)).Subscriptions);
    }

    [Fact]
    public async Task DrainReturnsPendingSignalsOnceInArrivalOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/draining", pinger, ct);
        var edge = file.AsReference<ICSharpFileEdge>();

        await pinger.Ping(1);
        await pinger.Ping(2);
        await Eventually(async () => (await file.Inspect(ct)).Subscriptions.Single().Pending == 2, ct);

        var drained = await edge.DrainPending(sandbox.LatestRunId, pinger.GetGrainId().ToString(), nameof(Pinged));
        Assert.Equal(2, drained.Signals.Length);
        Assert.Equal([1, 2], drained.Signals.Select(SignalJson.Number));
        await edge.Acknowledge(sandbox.LatestRunId, pinger.GetGrainId().ToString(), nameof(Pinged), drained.Watermark);
        Assert.Empty((await edge.DrainPending(sandbox.LatestRunId, pinger.GetGrainId().ToString(), nameof(Pinged))).Signals);
    }

    [Fact]
    public async Task TwoSubscriptionsKeepTheirSignalsApart()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var one = brain.Get<IPinger>("one");
        var two = brain.Get<IPinger>("two");
        var file = await Subscribed(brain, sandbox, "workspace/apart", one, ct);
        var edge = file.AsReference<ICSharpFileEdge>();
        await edge.Subscribed(sandbox.LatestRunId, two.GetGrainId().ToString(), nameof(Pinged));

        await two.Ping(9);
        await Eventually(async () => (await file.Inspect(ct)).Subscriptions.Any(s => s.Pending > 0), ct);

        Assert.Empty((await edge.DrainPending(sandbox.LatestRunId, one.GetGrainId().ToString(), nameof(Pinged))).Signals);
        Assert.Single((await edge.DrainPending(sandbox.LatestRunId, two.GetGrainId().ToString(), nameof(Pinged))).Signals);
    }

    [Fact]
    public async Task DrainingDoesNotClearUntilTheBatchIsAcknowledged()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/acknowledging", pinger, ct);
        var edge = file.AsReference<ICSharpFileEdge>();
        var source = pinger.GetGrainId().ToString();
        await using var arrivals = await brain.Observe<CSharpPendingArrived>(file, ct);
        await pinger.Ping(1);
        await pinger.Ping(2);
        await Arrivals(arrivals, 2, ct);

        var first = await edge.DrainPending(sandbox.LatestRunId, source, nameof(Pinged));
        var again = await edge.DrainPending(sandbox.LatestRunId, source, nameof(Pinged));

        Assert.Equal(2, first.Signals.Length);
        Assert.Equal(2, first.Watermark);
        Assert.Equal(first.Signals, again.Signals);

        await edge.Acknowledge(sandbox.LatestRunId, source, nameof(Pinged), first.Watermark);

        Assert.Empty((await edge.DrainPending(sandbox.LatestRunId, source, nameof(Pinged))).Signals);
        var view = Assert.Single((await file.Inspect(ct)).Subscriptions);
        Assert.Equal((0, 2L, 0L), (view.Pending, view.Delivered, view.Dropped));
    }

    [Fact]
    public async Task AnAcknowledgementNeverTrimsPastWhatIsPendingOrBelowWhatWasDelivered()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/clamped", pinger, ct);
        var edge = file.AsReference<ICSharpFileEdge>();
        var source = pinger.GetGrainId().ToString();
        await using var arrivals = await brain.Observe<CSharpPendingArrived>(file, ct);
        for (var number = 1; number <= 3; number++) { await pinger.Ping(number); }
        await Arrivals(arrivals, 3, ct);

        await edge.Acknowledge(sandbox.LatestRunId, source, nameof(Pinged), 1);
        var afterOne = Assert.Single((await file.Inspect(ct)).Subscriptions);
        Assert.Equal((2, 1L), (afterOne.Pending, afterOne.Delivered));

        await edge.Acknowledge(sandbox.LatestRunId, source, nameof(Pinged), 0);   // stale: nothing to do
        Assert.Equal(2, Assert.Single((await file.Inspect(ct)).Subscriptions).Pending);

        await edge.Acknowledge(sandbox.LatestRunId, source, nameof(Pinged), 99);  // forged: only what exists
        var settled = Assert.Single((await file.Inspect(ct)).Subscriptions);
        Assert.Equal((0, 3L), (settled.Pending, settled.Delivered));
    }

    [Fact]
    public async Task OverflowPastTheCapCountsTheDropAndPublishesFileChangedWhileTheCapItselfDropsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct, silo => silo.Services.PostConfigure<CSharpOptions>(options => options.MaximumPending = 3));
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/overflowing", pinger, ct);
        var edge = file.AsReference<ICSharpFileEdge>();
        var source = pinger.GetGrainId().ToString();
        await using var arrivals = await brain.Observe<CSharpPendingArrived>(file, ct);
        await using var changes = await brain.Observe<CSharpFileChanged>(file, ct);

        for (var number = 1; number <= 3; number++) { await pinger.Ping(number); }
        await Arrivals(arrivals, 3, ct);
        var atCap = Assert.Single((await file.Inspect(ct)).Subscriptions);
        Assert.Equal((3, 0L, 0L), (atCap.Pending, atCap.Delivered, atCap.Dropped));   // nothing is dropped below the cap

        await pinger.Ping(4);
        await Arrivals(arrivals, 1, ct);

        Assert.Equal("workspace/overflowing", (await changes.NextAsync(ct: ct)).FileId);   // the overflow is a published fact
        var overflowed = Assert.Single((await file.Inspect(ct)).Subscriptions);
        Assert.Equal((3, 1L, 1L), (overflowed.Pending, overflowed.Delivered, overflowed.Dropped));
        var batch = await edge.DrainPending(sandbox.LatestRunId, source, nameof(Pinged));
        Assert.Equal(4, batch.Watermark);
        Assert.Equal([2, 3, 4], batch.Signals.Select(SignalJson.Number));
    }

    [Fact]
    public async Task AnAcknowledgementOlderThanAnOverflowDoesNotDiscardTheNewestSignals()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct, silo => silo.Services.PostConfigure<CSharpOptions>(options => options.MaximumPending = 3));
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/racing", pinger, ct);
        var edge = file.AsReference<ICSharpFileEdge>();
        var source = pinger.GetGrainId().ToString();
        await using var arrivals = await brain.Observe<CSharpPendingArrived>(file, ct);
        for (var number = 1; number <= 3; number++) { await pinger.Ping(number); }
        await Arrivals(arrivals, 3, ct);
        var inFlight = await edge.DrainPending(sandbox.LatestRunId, source, nameof(Pinged));   // 1..3, watermark 3

        await pinger.Ping(4);                                                                 // drops 1
        await Arrivals(arrivals, 1, ct);
        await edge.Acknowledge(sandbox.LatestRunId, source, nameof(Pinged), inFlight.Watermark);

        var view = Assert.Single((await file.Inspect(ct)).Subscriptions);
        Assert.Equal((1, 3L, 1L), (view.Pending, view.Delivered, view.Dropped));
        Assert.Equal([4], (await edge.DrainPending(sandbox.LatestRunId, source, nameof(Pinged))).Signals.Select(SignalJson.Number));
    }

    [Fact]
    public void TheCapIsAnOptionThatMustBeAtLeastOne()
    {
        Assert.Equal(64, new CSharpOptions().MaximumPending);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CSharpOptions { MaximumPending = 0 }.Validate());
    }

    private static async Task Arrivals<T>(SignalProbe<T> probe, int count, CancellationToken ct) where T : Signal
    {
        for (var i = 0; i < count; i++) { await probe.NextAsync(ct: ct); }
    }

    [Fact]
    public async Task SubscribingRequiresARunThatSpeaksForTheFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = brain.Get<ICSharpFile>("workspace/unauthorized");
        await file.Write("Console.WriteLine(1);", ct);
        // Never started: no run id speaks for this file.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => file.AsReference<ICSharpFileEdge>().Subscribed("not-a-run", pinger.GetGrainId().ToString(), nameof(Pinged)));
    }

    [Fact]
    public async Task ASignalWithNoLiveRunWakesOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/waking", pinger, ct);

        sandbox.ExitLatest(0);                        // the registering run ends cleanly
        await Reconcile(file);                        // reconcile observes the exit
        Assert.True((await file.Inspect(ct)).ShouldRun); // ...but a subscribed file is waiting, not finished

        await pinger.Ping(1);
        await Eventually(() => sandbox.Started == 2, ct);   // a wake-run started
    }

    [Fact]
    public async Task ASecondSignalDuringAWakeRunQueuesInsteadOfStartingAnotherRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/queueing", pinger, ct);
        sandbox.ExitLatest(0);
        await Reconcile(file);

        await pinger.Ping(1);
        await Eventually(() => sandbox.Started == 2, ct);
        await pinger.Ping(2);                         // wake-run still running
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);

        Assert.Equal(2, sandbox.Started);             // no third run
        Assert.Equal(2, (await file.Inspect(ct)).Subscriptions.Single().Pending);
    }

    [Fact]
    public async Task ASubscribedFileStillWakesAfterItsActivationIsCollected()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/collected-sub", pinger, ct);
        sandbox.ExitLatest(0);
        await Reconcile(file);

        await brain.DeactivateAsync(file, ct);
        await pinger.Ping(1);

        await Eventually(() => sandbox.Started == 2, ct);
    }

    [Fact]
    public async Task FailingWakeRunsClearTheSubscriptionsAndDisarm()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/failing-sub", pinger, ct);
        sandbox.ExitLatest(0);
        await Reconcile(file);

        for (var attempt = 1; attempt <= CSharpFileNeuron.MaximumFailures; attempt++)
        {
            await pinger.Ping(attempt);
            await Eventually(() => sandbox.Started == attempt + 1, ct);
            sandbox.ExitLatest(1);
        }
        await pinger.Ping(0);
        await Eventually(async () => !(await file.Inspect(ct)).ShouldRun, ct);

        Assert.Empty((await file.Inspect(ct)).Subscriptions);
        Assert.Equal(CSharpFileNeuron.MaximumFailures + 1, sandbox.Started);
    }

    [Fact]
    public async Task StoppingClearsSubscriptionsAndNothingWakes()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/stopped-sub", pinger, ct);

        await file.Stop(ct);
        await pinger.Ping(1);
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);

        Assert.Empty((await file.Inspect(ct)).Subscriptions);
        Assert.Equal(1, sandbox.Started);             // only the original registering run ever started
    }

    [Fact]
    public async Task PendingLeftByALostRunIsRedeliveredByTheReconcile()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/lost-sub", pinger, ct);
        sandbox.ExitLatest(0);
        await Reconcile(file);

        await pinger.Ping(1);                         // buffered, wakes run 2; nothing drained it
        await Eventually(() => sandbox.Started == 2, ct);
        sandbox.LoseEveryRun();                       // the wake-run vanished with its container
        await Reconcile(file);

        await Eventually(() => sandbox.Started == 3, ct);   // the pending signal is not stranded
    }

    [Fact]
    public async Task AWakeRunIsNotToldWhichSignalWokeItBecauseTheStreamCarriesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/untriggered", pinger, ct);
        sandbox.ExitLatest(0);
        await Reconcile(file);

        await pinger.Ping(1);
        await Eventually(() => sandbox.Started == 2, ct);

        var wake = sandbox.Requests.Last(FakeSandbox.IsStart);
        Assert.Null(wake.Body!["environment"]!["CSharpFile__Trigger"]);
        Assert.Equal(1, Assert.Single((await file.Inspect(ct)).Subscriptions).Pending);   // the signal waits for the stream, not the environment
    }

    [Fact]
    public async Task ACleanWakeRunResetsTheFailureCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/recovering", pinger, ct);
        sandbox.ExitLatest(0);
        await Reconcile(file);

        await pinger.Ping(1);
        await Eventually(() => sandbox.Started == 2, ct);
        sandbox.ExitLatest(1);                        // one crash...
        await pinger.Ping(2);
        await Eventually(() => sandbox.Started == 3, ct);
        sandbox.ExitLatest(0);                        // ...then a clean wake-run
        await pinger.Ping(3);
        await Eventually(() => sandbox.Started == 4, ct);

        Assert.Equal(0, (await file.Inspect(ct)).Failures);
    }

    [Fact]
    public async Task AScriptCannotSubscribeToItsOwnFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var file = brain.Get<ICSharpFile>("workspace/self");
        await file.Write("Console.WriteLine(1);", ct);
        await file.Start(ct);

        await Assert.ThrowsAsync<ArgumentException>(() => file.AsReference<ICSharpFileEdge>()
            .Subscribed(sandbox.LatestRunId, file.GetGrainId().ToString(), nameof(CSharpFileChanged)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileWithAndWithoutSubscriptionsDisarmsAfterTheSameNumberOfFailedRuns(bool subscribed)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = brain.Get<ICSharpFile>("workspace/one-path/" + subscribed);
        await file.Write("Console.WriteLine(1);", ct);
        await file.Start(ct);
        if (subscribed)
        {
            await file.AsReference<ICSharpFileEdge>().Subscribed(sandbox.LatestRunId, pinger.GetGrainId().ToString(), nameof(Pinged));
            await using var arrivals = await brain.Observe<CSharpPendingArrived>(file, ct);
            await pinger.Ping(1);                       // waits for the stream; nothing drains it
            await arrivals.NextAsync(ct: ct);
        }

        while ((await file.Inspect(ct)).ShouldRun)
        {
            sandbox.ExitLatest(1);
            await Reconcile(file);
        }

        var disarmed = await file.Inspect(ct);
        Assert.Equal(CSharpFileNeuron.MaximumFailures, sandbox.Started);
        Assert.Equal(CSharpFileNeuron.MaximumFailures, disarmed.Failures);
        Assert.Empty(disarmed.Subscriptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStaleRunTokenStopsSpeakingAfterARestart(bool subscribed)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = brain.Get<ICSharpFile>("workspace/stale/" + subscribed);
        await file.Write("Console.WriteLine(1);", ct);
        await file.Start(ct);
        var first = sandbox.LatestRunId;
        var edge = file.AsReference<ICSharpFileEdge>();
        if (subscribed) { await edge.Subscribed(first, pinger.GetGrainId().ToString(), nameof(Pinged)); }

        await file.Swap("Console.WriteLine(2);", ct);

        Assert.NotEqual(first, sandbox.LatestRunId);
        Assert.False((await edge.Authorize(first)).Active);
        Assert.True((await edge.Authorize(sandbox.LatestRunId)).Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileWhoseRunIsLostWhileSomethingIsDueReportsRestarting(bool subscribed)
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var file = brain.Get<ICSharpFile>("workspace/restarting/" + subscribed);
        await file.Write("Console.WriteLine(1);", ct);
        await file.Start(ct);
        if (subscribed)
        {
            await file.AsReference<ICSharpFileEdge>().Subscribed(sandbox.LatestRunId, pinger.GetGrainId().ToString(), nameof(Pinged));
            await using var arrivals = await brain.Observe<CSharpPendingArrived>(file, ct);
            await pinger.Ping(1);
            await arrivals.NextAsync(ct: ct);
        }

        sandbox.LoseEveryRun();

        Assert.Equal(CSharpFileStatus.Restarting, (await file.Inspect(ct)).Status);
    }

    [Fact]
    public async Task ASubscribedFileThatFinishedCleanlyWithNothingPendingIsWaitingNotRestarting()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct);
        var file = await Subscribed(brain, sandbox, "workspace/waiting", brain.Get<IPinger>("pinger"), ct);

        sandbox.ExitLatest(0);

        var waiting = await file.Inspect(ct);
        Assert.Equal(CSharpFileStatus.Exited, waiting.Status);
        Assert.True(waiting.ShouldRun);
    }

    [Fact]
    public async Task SwappingKeepsTheWatermarkAndTheDropCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct, silo => silo.Services.PostConfigure<CSharpOptions>(options => options.MaximumPending = 2));
        var pinger = brain.Get<IPinger>("pinger");
        var file = await Subscribed(brain, sandbox, "workspace/swap-watermark", pinger, ct);
        var edge = file.AsReference<ICSharpFileEdge>();
        await using var arrivals = await brain.Observe<CSharpPendingArrived>(file, ct);
        for (var number = 1; number <= 3; number++) { await pinger.Ping(number); }   // 1 dropped
        await Arrivals(arrivals, 3, ct);
        await edge.Acknowledge(sandbox.LatestRunId, pinger.GetGrainId().ToString(), nameof(Pinged), 2);

        await file.Swap("Console.WriteLine(2);", ct);

        var view = Assert.Single((await file.Inspect(ct)).Subscriptions);
        Assert.Equal((1, 2L, 1L), (view.Pending, view.Delivered, view.Dropped));
    }

    internal static Task Reconcile(ICSharpFile file)
        => file.AsReference<IRemindable>().ReceiveReminder(CSharpFileNeuron.ReconcileReminder, default);

    // Starts the file (always-on) so a real run id exists, then registers the subscription as that run.
    internal static async Task<ICSharpFile> Subscribed(ModuleBrain brain, FakeSandbox sandbox, string id, IPinger source, CancellationToken ct)
    {
        var file = brain.Get<ICSharpFile>(id);
        await file.Write("Console.WriteLine(1);", ct);
        await file.Start(ct);
        await file.AsReference<ICSharpFileEdge>().Subscribed(sandbox.LatestRunId, source.GetGrainId().ToString(), nameof(Pinged));
        return file;
    }

    private static Task Eventually(Func<bool> condition, CancellationToken ct) => Eventually(() => Task.FromResult(condition()), ct);

    private static async Task Eventually(Func<Task<bool>> condition, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await condition()) { await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token); }
    }
}
