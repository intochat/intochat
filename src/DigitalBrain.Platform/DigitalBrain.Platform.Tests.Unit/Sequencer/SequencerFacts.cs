using DigitalBrain.Platform.Sequencer;

namespace DigitalBrain.Platform.Tests.Unit.Sequencer;

public sealed class SequencerFacts
{
    internal static MachineIdentity Machine() => MachineIdentity.LoadOrCreate(Path.Combine(Path.GetTempPath(), $"m-{Guid.NewGuid():N}.key"));

    internal static HeadRecord Record(MachineIdentity machine, string partHash, long version, TimeSpan lease, byte[]? payload = null)
    {
        var record = new HeadRecord
        {
            PartHash = partHash, Version = version, Holder = machine.MachineId, HolderKey = machine.PublicKey,
            LeaseExpiry = DateTimeOffset.UtcNow + lease, Payload = payload ?? [],
        };
        record.Signature = machine.Sign(record.SignedBytes());
        return record;
    }

    [Fact(Timeout = 60_000)]
    public async Task VersionsAdvanceOnlyByCompareAndSet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var machine = Machine();
        var sequencer = brain.Grains.GetGrain<ISequencerGrain>("p1");

        Assert.True(await sequencer.CompareAndSet(0, Record(machine, "p1", 1, TimeSpan.FromMinutes(1))));
        Assert.False(await sequencer.CompareAndSet(0, Record(machine, "p1", 1, TimeSpan.FromMinutes(1))));
        Assert.True(await sequencer.CompareAndSet(1, Record(machine, "p1", 2, TimeSpan.FromMinutes(1))));
        Assert.Equal(2, (await sequencer.Get())!.Version);
    }

    [Fact(Timeout = 60_000)]
    public async Task AForgedOrTamperedRecordIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var machine = Machine();
        var sequencer = brain.Grains.GetGrain<ISequencerGrain>("p1");
        var record = Record(machine, "p1", 1, TimeSpan.FromMinutes(1), [1]);
        record.Payload = [2];

        Assert.False(await sequencer.CompareAndSet(0, record));
        Assert.Null(await sequencer.Get());
    }

    [Fact(Timeout = 60_000)]
    public async Task AnotherMachineCannotTakeALiveLeaseButCanTakeAnExpiredOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var alice = Machine();
        using var bob = Machine();
        var live = brain.Grains.GetGrain<ISequencerGrain>("live");
        var expired = brain.Grains.GetGrain<ISequencerGrain>("expired");
        await live.CompareAndSet(0, Record(alice, "live", 1, TimeSpan.FromMinutes(1)));
        await expired.CompareAndSet(0, Record(alice, "expired", 1, TimeSpan.FromSeconds(-1)));

        Assert.False(await live.CompareAndSet(1, Record(bob, "live", 2, TimeSpan.FromMinutes(1))));
        Assert.True(await expired.CompareAndSet(1, Record(bob, "expired", 2, TimeSpan.FromMinutes(1))));
    }

    [Fact(Timeout = 60_000)]
    public async Task ADifferentKeyCannotTakeALiveLeaseUnderTheSameHolderName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        using var alice = Machine();
        using var mallory = Machine();
        var sequencer = brain.Grains.GetGrain<ISequencerGrain>("p1");
        await sequencer.CompareAndSet(0, Record(alice, "p1", 1, TimeSpan.FromMinutes(1)));
        var forged = new HeadRecord
        {
            PartHash = "p1", Version = 2, Holder = alice.MachineId, HolderKey = mallory.PublicKey,
            LeaseExpiry = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1),
        };
        forged.Signature = mallory.Sign(forged.SignedBytes());

        Assert.False(await sequencer.CompareAndSet(1, forged));
        Assert.Equal(1, (await sequencer.Get())!.Version);
    }

    [Fact(Timeout = 60_000)]
    public async Task ARecordWithAGarbageHolderKeyIsRefusedAndNothingIsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var sequencer = brain.Grains.GetGrain<ISequencerGrain>("p1");
        var record = new HeadRecord { PartHash = "p1", Version = 1, Holder = "x", HolderKey = [1, 2, 3], Signature = [4] };

        Assert.False(await sequencer.CompareAndSet(0, record));
        Assert.Null(await sequencer.Get());
    }
}
