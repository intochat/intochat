using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

// On a machine a durable subscription's watermark is its source's part-log seq: what the runtime buffers comes
// from the source's log after that seq, once per entry, whether or not a live notification carried it.
public sealed class PartLogReplayFacts
{
    private static string Brain => BrainScope.Create("owner", "owner").Id;
    private static string Echo => Brain + "/apps/echo";
    private static string Behavior => Echo + "/synapses/a.cs";
    private static string Source => Brain + "/apps/source/pinger";

    private static async Task<(ITestBrain Brain, string Session)> StartAsync(CancellationToken ct)
    {
        var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, partLog: true);
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        return (brain, (await Runtime(brain).Inspect(ct)).SessionId!);
    }

    private static async Task<int[]> PendingNumbers(ITestBrain brain, string session)
        => [.. (await Edge(brain).PendingBatches(session)).SelectMany(b => b.Signals).Select(SignalJson.Number)];

    [Fact]
    public async Task ANewSubscriptionStartsAtItsSourcesCurrentSeq()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, session) = await StartAsync(ct);
        await using var _ = brain;
        var pinger = brain.Get<IPinger>(Source);
        await pinger.Ping(1);
        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);

        await pinger.Ping(2);
        await changes.NextAsync(ct: ct);

        var pending = await PendingNumbers(brain, session);
        Assert.Equal([2], pending);
    }

    [Fact]
    public async Task ASignalJournalledWithoutANotificationArrivesWithTheNextLiveOneOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, session) = await StartAsync(ct);
        await using var _ = brain;
        var pinger = brain.Get<IPinger>(Source);
        await pinger.Ping(1);
        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await brain.Services.GetRequiredService<ISignalJournal>().Append(pinger.GetGrainId(), [new Pinged(7)]);

        await pinger.Ping(8);
        await changes.NextAsync(ct: ct);

        var pending = await PendingNumbers(brain, session);
        Assert.Equal([7, 8], pending);
    }

    [Fact]
    public async Task SignalsJournalledWhileTheRuntimeSleptAreDeliveredWhenItReactivates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, session) = await StartAsync(ct);
        await using var _ = brain;
        var pinger = brain.Get<IPinger>(Source);
        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await brain.DeactivateAsync(Runtime(brain), ct);
        await brain.Services.GetRequiredService<ISignalJournal>().Append(pinger.GetGrainId(), [new Pinged(5)]);

        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        var pending = await PendingAfterResume(brain, changes, ct);

        Assert.Equal([5], pending);
    }

    [Fact]
    public async Task AReactivatedRuntimeDoesNotRedeliverWhatItBuffered()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, session) = await StartAsync(ct);
        await using var _ = brain;
        var pinger = brain.Get<IPinger>(Source);
        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await brain.Services.GetRequiredService<ISignalJournal>().Append(pinger.GetGrainId(), [new Pinged(1)]);
        await brain.DeactivateAsync(Runtime(brain), ct);
        await using (var first = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct)) { await PendingAfterResume(brain, first, ct); }
        await brain.DeactivateAsync(Runtime(brain), ct);
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);

        await pinger.Ping(2);
        int[] pending;
        do { await changes.NextAsync(ct: ct); pending = await PendingNumbers(brain, (await Runtime(brain).Inspect(ct)).SessionId!); }
        while (!pending.Contains(2));

        Assert.Equal([1, 2], pending);
    }

    [Fact]
    public async Task ASourceWhosePartIsNotLeasedHereFailsTheSubscribeAsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, journal: new UnleasedJournal());
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var pinger = brain.Get<IPinger>(Source);

        await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged)));
    }

    // A nudge the runtime heard but could not act on (its arrival expired in the queue or its log read failed) is
    // read from the source's log by the next reminder, not left until the source publishes again.
    [Fact]
    public async Task ANudgeThatWasNotReadIsDeliveredByTheNextReminderWithoutAnotherPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = new ReadOnceFailingJournal();
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, journal: journal);
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        var session = (await Runtime(brain).Inspect(ct)).SessionId!;
        var pinger = brain.Get<IPinger>(Source);
        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        var failed = journal.FailNextReadOf(pinger.GetGrainId());

        await pinger.Ping(1);
        await failed.WaitAsync(ct);
        Assert.Empty(await PendingNumbers(brain, session));
        await Runtime(brain).AsReference<IRemindable>().ReceiveReminder(BrainRuntimeNeuron.ReconcileReminder, default);

        var pending = await PendingNumbers(brain, session);
        Assert.Equal([1], pending);
    }

    // Waits, change by change, until the resumed runtime's catch-up has buffered something.
    private static async Task<int[]> PendingAfterResume(ITestBrain brain, SignalProbe<BrainRuntimeChanged> changes, CancellationToken ct)
    {
        while (true)
        {
            var pending = await PendingNumbers(brain, (await Runtime(brain).Inspect(ct)).SessionId!);
            if (pending.Length > 0) { return pending; }
            await changes.NextAsync(ct: ct);
        }
    }

    private sealed class UnleasedJournal : ISignalJournal
    {
        public Task Append(GrainId publisher, Signal[] signals) => Task.CompletedTask;
        public Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit) => Task.FromResult<JournaledSignal[]>([]);
        public Task<PartSignals?> SignalsOf(GrainId publisher, long since)
            => throw new ServiceUnavailableException($"The part of {publisher} is leased by another machine.");
    }

    // A part log in memory, one seq per publisher, whose next read of an armed publisher fails once.
    private sealed class ReadOnceFailingJournal : ISignalJournal
    {
        private readonly Dictionary<GrainId, List<Signal>> _logs = [];
        private (GrainId Publisher, TaskCompletionSource Failed)? _armed;

        public Task FailNextReadOf(GrainId publisher)
        {
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_logs) { _armed = (publisher, failed); }
            return failed.Task;
        }

        public Task Append(GrainId publisher, Signal[] signals)
        {
            lock (_logs) { (_logs.TryGetValue(publisher, out var log) ? log : _logs[publisher] = []).AddRange(signals); }
            return Task.CompletedTask;
        }

        public Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit) => Task.FromResult<JournaledSignal[]>([]);

        public Task<PartSignals?> SignalsOf(GrainId publisher, long since)
        {
            lock (_logs)
            {
                if (_armed is { } armed && armed.Publisher == publisher)
                {
                    _armed = null;
                    armed.Failed.SetResult();
                    throw new ServiceUnavailableException($"The part of {publisher} could not be read.");
                }
                var log = _logs.TryGetValue(publisher, out var signals) ? signals : [];
                JournaledSignal[] after = [.. log.Select((signal, index) => new JournaledSignal(index + 1, signal)).Where(entry => entry.Sequence > since)];
                return Task.FromResult<PartSignals?>(new PartSignals(0, log.Count, after));
            }
        }
    }

    private static IBrainPrograms Programs(ITestBrain brain) => RuntimeBrain.Programs(brain, Brain);

    private static IBrainRuntime Runtime(ITestBrain brain) => RuntimeBrain.Runtime(brain, Brain);

    private static IBrainRuntimeEdge Edge(ITestBrain brain) => RuntimeBrain.Edge(brain, Brain);
}
