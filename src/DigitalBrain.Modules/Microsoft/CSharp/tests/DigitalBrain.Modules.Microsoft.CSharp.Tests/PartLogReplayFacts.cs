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

    private static IBrainPrograms Programs(ModuleBrain brain) => brain.Grains().GetGrain<IBrainPrograms>(BrainRuntimeKeys.For(Brain));

    private static IBrainRuntime Runtime(ModuleBrain brain) => brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(Brain));

    private static IBrainRuntimeEdge Edge(ModuleBrain brain) => brain.Grains().GetGrain<IBrainRuntimeEdge>(BrainRuntimeKeys.For(Brain));
}
