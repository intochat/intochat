using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime.Signals;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// A durable subscription's watermark is its source's sequence in the brain's log: what the runtime buffers comes from the source's log
// after that sequence, once per entry, whether or not the runtime was awake when it was written (06-08: every source is a plain neuron
// read from the BrainLog; the legacy part-log journal and its notifications are gone).
public sealed class PartLogReplayFacts
{
    private static string Brain => BrainScope.Create("owner", "owner").Id;
    private static string Echo => Brain + "/apps/echo";
    private static string Behavior => Echo + "/synapses/a.cs";
    private static string Source => Brain + "/apps/source/pinger";

    private static async Task<(ITestBrain Brain, string Session)> StartAsync(CancellationToken ct)
    {
        var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        await Programs(brain).Assign(Echo, new() { ["synapses/a.cs"] = RuntimeBrain.Idle }, []);
        return (brain, (await Runtime(brain).Inspect(ct)).SessionId!);
    }

    private static async Task<int[]> PendingNumbers(ITestBrain brain, string session)
        => [.. (await Edge(brain).PendingBatches(session)).SelectMany(b => b.Signals).Select(SignalJson.Number)];

    [Trait("host", "own")]
    [Fact]
    public async Task ANewSubscriptionStartsAtItsSourcesCurrentSeq()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, session) = await StartAsync(ct);
        await using var _ = brain;
        var pinger = brain.Get<IPinger>(Source);
        await pinger.Ping(1);
        await Edge(brain).Subscribed(session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);

        await pinger.Ping(2);
        await changes.NextAsync(ct: ct);

        var pending = await PendingNumbers(brain, session);
        Assert.Equal([2], pending);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SignalsJournalledWhileTheRuntimeSleptAreDeliveredWhenItReactivates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, session) = await StartAsync(ct);
        await using var _ = brain;
        var pinger = brain.Get<IPinger>(Source);
        await Edge(brain).Subscribed(session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await brain.DeactivateAsync(Runtime(brain), ct);
        await pinger.Ping(5);

        await using var changes = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct);
        var pending = await PendingAfterResume(brain, changes, ct);

        Assert.Equal([5], pending);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AReactivatedRuntimeDoesNotRedeliverWhatItBuffered()
    {
        var ct = TestContext.Current.CancellationToken;
        var (brain, session) = await StartAsync(ct);
        await using var _ = brain;
        var pinger = brain.Get<IPinger>(Source);
        await Edge(brain).Subscribed(session, Behavior, RuntimeBrain.Source(pinger), Pings.Pinged);
        await using (var buffered = await brain.Observe<BrainRuntimeChanged>(Runtime(brain), ct))
        {
            await pinger.Ping(1);
            await PendingAfterResume(brain, buffered, ct);
        }
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

    // Waits, change by change, until the resumed runtime's catch-up has buffered something.
    private static async Task<int[]> PendingAfterResume(ITestBrain brain, PlainSignalProbe<BrainRuntimeChanged> changes, CancellationToken ct)
    {
        while (true)
        {
            var pending = await PendingNumbers(brain, (await Runtime(brain).Inspect(ct)).SessionId!);
            if (pending.Length > 0) { return pending; }
            await changes.NextAsync(ct: ct);
        }
    }

    private static IBrainPrograms Programs(ITestBrain brain) => RuntimeBrain.Programs(brain, Brain);

    private static IBrainRuntime Runtime(ITestBrain brain) => RuntimeBrain.Runtime(brain, Brain);

    private static IBrainRuntimeEdge Edge(ITestBrain brain) => RuntimeBrain.Edge(brain, Brain);
}
