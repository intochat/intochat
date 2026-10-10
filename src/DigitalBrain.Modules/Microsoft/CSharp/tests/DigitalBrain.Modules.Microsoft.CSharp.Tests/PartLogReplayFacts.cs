using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

// On a machine a durable subscription's watermark is its source's part-log seq: what the runtime buffers comes
// from the source's log after that seq, once per entry, whether or not a live notification carried it.
public sealed class PartLogReplayFacts
{
    private static string Brain => BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id;
    private static string Echo => Brain + "/apps/echo";
    private static string Behavior => Echo + "/synapses/a.cs";
    private static string Source => Brain + "/apps/source/pinger";

    private static async Task<(ModuleBrain Brain, string Session)> StartAsync(CancellationToken ct)
    {
        var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, partLog: true);
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        return (brain, (await Runtime(brain).Inspect(ct)).SessionId!);
    }

    private static async Task<int[]> PendingNumbers(ModuleBrain brain, string session)
        => [.. (await Edge(brain).PendingBatches(session)).SelectMany(b => b.Signals).Select(SignalJson.Number)];

    [Fact(Timeout = 60_000)]
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

    [Fact(Timeout = 60_000)]
    public async Task ASignalJournalledWithoutANotificationArrivesWithTheNextLiveOneOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, session) = await StartAsync(ct);
        await using var _ = brain;
        var pinger = brain.Get<IPinger>(Source);
        await pinger.Ping(1);
        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        await brain.SiloServices.GetRequiredService<ISignalJournal>().Append(pinger.GetGrainId(), [new Pinged(7)]);

        await pinger.Ping(8);
        await changes.NextAsync(ct: ct);

        var pending = await PendingNumbers(brain, session);
        Assert.Equal([7, 8], pending);
    }

    [Fact(Timeout = 60_000)]
    public async Task SignalsJournalledWhileTheRuntimeSleptAreDeliveredWhenItReactivates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, session) = await StartAsync(ct);
        await using var _ = brain;
        var pinger = brain.Get<IPinger>(Source);
        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await brain.DeactivateAsync(Runtime(brain), ct);
        await brain.SiloServices.GetRequiredService<ISignalJournal>().Append(pinger.GetGrainId(), [new Pinged(5)]);

        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        var pending = await PendingAfterResume(brain, changes, ct);

        Assert.Equal([5], pending);
    }

    [Fact(Timeout = 60_000)]
    public async Task AReactivatedRuntimeDoesNotRedeliverWhatItBuffered()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, session) = await StartAsync(ct);
        await using var _ = brain;
        var pinger = brain.Get<IPinger>(Source);
        await Edge(brain).Subscribed(session, Behavior, pinger.GetGrainId().ToString(), nameof(Pinged));
        await brain.SiloServices.GetRequiredService<ISignalJournal>().Append(pinger.GetGrainId(), [new Pinged(1)]);
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

    [Fact(Timeout = 60_000)]
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

    // Waits, change by change, until the resumed runtime's catch-up has buffered something.
    private static async Task<int[]> PendingAfterResume(ModuleBrain brain, SignalProbe<BrainRuntimeChanged> changes, CancellationToken ct)
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

    private static IBrainPrograms Programs(ModuleBrain brain) => brain.Grains().GetGrain<IBrainPrograms>(BrainRuntimeKeys.For(Brain));

    private static IBrainRuntime Runtime(ModuleBrain brain) => brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(Brain));

    private static IBrainRuntimeEdge Edge(ModuleBrain brain) => brain.Grains().GetGrain<IBrainRuntimeEdge>(BrainRuntimeKeys.For(Brain));
}
