using System.Security.Cryptography;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

// A durable subscription's watermark is a part-log seq: the journal reads one publisher's signals after it.
public sealed class PublisherSignalsFacts
{
    private static string Scope => BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

    private static string Counter => $"{Scope}/apps/counter/s1";

    [Fact(Timeout = 60_000)]
    public async Task APublishersSignalsAreReadAfterASeqWithTheirEntrySeq()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().AsMachine().StartAsync(ct);
        var counter = brain.Get<ISignalCounter>(Counter);
        await counter.Change(1);
        await counter.Ping();
        await counter.Change(2);
        var journal = brain.Services.GetRequiredService<ISignalJournal>();

        var all = (await journal.SignalsOf(counter.GetGrainId(), 0))!;
        var after = (await journal.SignalsOf(counter.GetGrainId(), all.Signals[0].Sequence))!;

        Assert.Equal(["Changed", "Pinged", "Changed"], all.Signals.Select(s => s.Signal.GetType().Name));
        Assert.Equal(all.Signals.Select(s => s.Sequence).Order(), all.Signals.Select(s => s.Sequence));
        Assert.Equal(all.Signals.Skip(1).Select(s => s.Sequence), after.Signals.Select(s => s.Sequence));
        Assert.Equal(all.Signals[^1].Sequence, all.Last);
        Assert.Equal(0, all.Floor);
    }

    [Fact(Timeout = 60_000)]
    public async Task APartHeldByAnotherMachineIsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().AsMachine().StartAsync(ct);
        var held = $"{Scope}/apps/held/s1";
        var part = PartId.Of(held);
        var key = await brain.Services.GetRequiredService<IPartKeys>().Key(part, AccountSession.DefaultLogin, ct);
        using var rival = SequencerFacts.Machine();
        var taken = await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), rival, PartLeaseFacts.Seen(), new PartsOptions { Role = MachineRole.Cloud },
            TimeProvider.System, brain.Services.GetRequiredService<Serializer>());

        await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            brain.Services.GetRequiredService<ISignalJournal>().SignalsOf(GrainId.Create("test.signal-counter", held), 0));
        await taken.Release();
    }

    [Fact(Timeout = 60_000)]
    public async Task ASnapshotRaisesTheFloorAReopenedLogServesFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PartLogFacts.StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var options = PartSnapshotFacts.Options();

        var (log, _) = await PartSnapshotFacts.OpenAsync(brain, part, key, machine, options, ct);
        await using (log)
        {
            for (var i = 1; i <= 4; i++) { await log.Append("g1", "s", RecordChange.Write, [(byte)i], [new Probed(i.ToString())]); }
            Assert.Equal(2, log.SignalsFloor);
        }
        var (reopened, _) = await PartSnapshotFacts.OpenAsync(brain, part, key, machine, options, ct);
        await using var __ = reopened;

        Assert.Equal(2, reopened.SignalsFloor);
        Assert.Equal(["3", "4"], reopened.SignalsSince(0).SelectMany(e => e.Signals).OfType<Probed>().Select(p => p.Value));
    }

    [Fact(Timeout = 60_000)]
    public async Task PlainHostsHaveNothingToReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var counter = brain.Get<ISignalCounter>(Counter);
        await counter.Change(1);

        Assert.Null(await brain.Services.GetRequiredService<ISignalJournal>().SignalsOf(counter.GetGrainId(), 0));
    }
}
