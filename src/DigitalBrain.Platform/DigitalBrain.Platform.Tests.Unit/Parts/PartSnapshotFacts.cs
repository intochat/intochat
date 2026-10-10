using System.Security.Cryptography;
using DigitalBrain.Framework.Mesh;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class PartSnapshotFacts
{
    private static PartsOptions Options(int snapshotEvery = 2) => new()
    {
        Role = MachineRole.Cloud, SnapshotEvery = snapshotEvery, BatchDelay = TimeSpan.FromMilliseconds(5),
        MachineDirectory = Path.Combine(Path.GetTempPath(), $"machine-{Guid.NewGuid():N}"),
    };

    private static async Task<(PartLog Log, PartLease Lease)> OpenAsync(ModuleBrain brain, PartId part, byte[] key, MachineIdentity machine, PartsOptions options,
        CancellationToken ct)
    {
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var lease = await PartLease.Acquire(part, key, brain.Grains, machine, PartLeaseFacts.Seen(), options, TimeProvider.System, serializer);
        var log = await PartLog.Open(part, key, lease, brain.SiloServices.GetRequiredService<IMeshStore>(), serializer, options, TimeProvider.System,
            NullLogger<PartLog>.Instance, ct);
        return (log, lease);
    }

    [Fact(Timeout = 60_000)]
    public async Task ASnapshotReplacesTheChainAndReopeningReadsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PartLogFacts.StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var options = Options();

        var (log, _) = await OpenAsync(brain, part, key, machine, options, ct);
        await using (log)
        {
            await log.Append("g1", "s", RecordChange.Write, [1], []);
            await log.Append("g2", "s", RecordChange.Write, [2], []);
            await log.Append("g1", "s", RecordChange.Write, [3], []);
        }
        var (reopened, lease) = await OpenAsync(brain, part, key, machine, options, ct);
        await using var _ = reopened;

        Assert.Equal([3], reopened.Read("g1", "s")!.Record);
        Assert.Equal(3, reopened.Read("g1", "s")!.ETagSeq);
        Assert.Equal([2], reopened.Read("g2", "s")!.Record);
        Assert.Equal(3, reopened.LastSeq);
        Assert.NotNull(lease.Payload.Snapshot);
        Assert.Equal(2, lease.Payload.SnapshotSeq);
        Assert.Equal(1, lease.Payload.SegmentsSinceSnapshot);
    }

    [Fact(Timeout = 60_000)]
    public async Task ReplacedSegmentsAreDiscarded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PartLogFacts.StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var mesh = brain.SiloServices.GetRequiredService<IMeshStore>();
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var (log, lease) = await OpenAsync(brain, part, key, machine, Options(snapshotEvery: 3), ct);
        await using var _ = log;

        await log.Append("g1", "s", RecordChange.Write, [1], []);
        var first = lease.Payload.Head!;
        await log.Append("g1", "s", RecordChange.Write, [2], []);
        var second = lease.Payload.Head!;
        await log.Append("g1", "s", RecordChange.Write, [3], []);

        Assert.Null(lease.Payload.Head);
        Assert.NotNull(lease.Payload.Snapshot);
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => mesh.Read(key, first, ct));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => mesh.Read(key, second, ct));
        var snapshot = serializer.Deserialize<PartSnapshot>(await mesh.Read(key, lease.Payload.Snapshot!, ct))!;
        Assert.Equal(3, snapshot.Seq);
    }

    [Fact(Timeout = 60_000)]
    public async Task ASecondSnapshotDiscardsTheFirstAndTheSegmentsSinceIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PartLogFacts.StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var mesh = brain.SiloServices.GetRequiredService<IMeshStore>();
        var (log, lease) = await OpenAsync(brain, part, key, machine, Options(), ct);
        await using var _ = log;

        await log.Append("g1", "s", RecordChange.Write, [1], []);
        await log.Append("g1", "s", RecordChange.Write, [2], []);
        var firstSnapshot = lease.Payload.Snapshot!;
        await log.Append("g1", "s", RecordChange.Write, [3], []);
        var segment = lease.Payload.Head!;
        await log.Append("g1", "s", RecordChange.Write, [4], []);

        Assert.NotEqual(firstSnapshot, lease.Payload.Snapshot);
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => mesh.Read(key, firstSnapshot, ct));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => mesh.Read(key, segment, ct));
    }

    [Fact(Timeout = 60_000)]
    public async Task SignalsBeforeASnapshotAreStillServedAfterReopening()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PartLogFacts.StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var options = Options();

        var (log, _) = await OpenAsync(brain, part, key, machine, options, ct);
        await using (log)
        {
            await log.Append("g1", "s", RecordChange.Write, [1], [new Probed("one")]);
            await log.Append("g1", "s", RecordChange.Write, [2], [new Probed("two")]);
            await log.Append("g1", "s", RecordChange.Write, [3], [new Probed("three")]);
        }
        var (reopened, _) = await OpenAsync(brain, part, key, machine, options, ct);
        await using var __ = reopened;

        Assert.Equal(["one", "two", "three"], reopened.SignalsSince(0).SelectMany(e => e.Signals).OfType<Probed>().Select(p => p.Value));
    }
}
