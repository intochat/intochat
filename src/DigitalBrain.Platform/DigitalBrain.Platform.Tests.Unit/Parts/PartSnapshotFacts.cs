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
    internal static PartsOptions Options(int snapshotEvery = 2) => new()
    {
        Role = MachineRole.Cloud, SnapshotEvery = snapshotEvery, BatchDelay = TimeSpan.FromMilliseconds(5),
        MachineDirectory = Path.Combine(Path.GetTempPath(), $"machine-{Guid.NewGuid():N}"),
    };

    internal static async Task<(PartLog Log, PartLease Lease)> OpenAsync(ITestBrain brain, PartId part, byte[] key, MachineIdentity machine, PartsOptions options,
        CancellationToken ct)
    {
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var lease = await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), machine, PartLeaseFacts.Seen(), options, TimeProvider.System, serializer);
        var log = await PartLog.Open(part, key, lease, brain.Services.GetRequiredService<IMeshStore>(), serializer, options, TimeProvider.System,
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
        var mesh = brain.Services.GetRequiredService<IMeshStore>();
        var serializer = brain.Services.GetRequiredService<Serializer>();
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
        var mesh = brain.Services.GetRequiredService<IMeshStore>();
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

    [Fact(Timeout = 60_000)]
    public async Task AfterTwoSnapshotsTheLogServesSignalsOnlyAboveTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PartLogFacts.StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var (log, lease) = await OpenAsync(brain, part, key, machine, Options(), ct);
        await using var _ = log;

        foreach (var value in new[] { "one", "two", "three", "four", "five" }) { await log.Append("g1", "s", RecordChange.Write, [1], [new Probed(value)]); }

        Assert.Equal(4, lease.Payload.SnapshotSeq);
        var (floor, last, entries) = log.SignalsOf("g1", 0);
        Assert.Equal(2, floor);
        Assert.Equal(5, last);
        Assert.Equal([3L, 4L, 5L], entries.Select(e => e.Seq));
        Assert.Equal([3L, 4L, 5L], log.SignalsSince(0).Select(e => e.Seq));
    }

    [Fact(Timeout = 60_000)]
    public async Task AnOwnMachineWhoseQueueNeverEmptiesStillSnapshotsItsCommittedPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await PartLogFacts.StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var serializer = brain.Services.GetRequiredService<Serializer>();
        var options = Options(snapshotEvery: 1);
        options.Role = MachineRole.Own;
        var mesh = new HeldMesh(brain.Services.GetRequiredService<IMeshStore>());
        var lease = await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), machine, PartLeaseFacts.Seen(), options, TimeProvider.System, serializer);
        await using var log = await PartLog.Open(part, key, lease, mesh, serializer, options, TimeProvider.System, NullLogger<PartLog>.Instance, ct);

        await log.Append("g1", "s", RecordChange.Write, [1], []);
        await mesh.Entered(PartSegments.SegmentId(part, 1)).WaitAsync(ct);
        await log.Append("g2", "s", RecordChange.Write, [2], []);
        mesh.Release(PartSegments.SegmentId(part, 1));
        // The second batch is held, so the queue is not empty while the first batch commits and snapshots.
        await mesh.Entered(PartSegments.SegmentId(part, 2)).WaitAsync(ct);

        Assert.Equal(1, lease.Payload.SnapshotSeq);
        var snapshot = serializer.Deserialize<PartSnapshot>(await mesh.Read(key, lease.Payload.Snapshot!, ct))!;
        Assert.Equal(["g1"], snapshot.Records.Select(r => r.GrainId));
        Assert.Equal([2], log.Read("g2", "s")!.Record);
        mesh.Release(PartSegments.SegmentId(part, 2));
        await log.Flushed();
    }

    // Holds each segment write until released, and tells when a write arrived.
    private sealed class HeldMesh(IMeshStore inner) : IMeshStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (TaskCompletionSource Entered, TaskCompletionSource Released)> _held = new();

        private (TaskCompletionSource Entered, TaskCompletionSource Released) Gate(string segmentId) =>
            _held.GetOrAdd(segmentId, _ => (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)));

        public Task Entered(string segmentId) => Gate(segmentId).Entered.Task;

        public void Release(string segmentId) => Gate(segmentId).Released.TrySetResult();

        public async Task<MeshRef> Write(byte[] key, string segmentId, byte[] plaintext, CancellationToken cancellationToken = default)
        {
            if (segmentId.Contains("/log/", StringComparison.Ordinal))
            {
                var gate = Gate(segmentId);
                gate.Entered.TrySetResult();
                await gate.Released.Task;
            }
            return await inner.Write(key, segmentId, plaintext, cancellationToken);
        }

        public Task<byte[]> Read(byte[] key, MeshRef reference, CancellationToken cancellationToken = default) => inner.Read(key, reference, cancellationToken);

        public Task<ShardAudit[]> Audit(byte[] key, MeshRef reference, int round, CancellationToken cancellationToken = default) =>
            inner.Audit(key, reference, round, cancellationToken);

        public Task<MeshRepair> Repair(byte[] key, MeshRef reference, bool reissue = false, CancellationToken cancellationToken = default) =>
            inner.Repair(key, reference, reissue, cancellationToken);

        public Task Discard(byte[] key, MeshRef reference, CancellationToken cancellationToken = default) => inner.Discard(key, reference, cancellationToken);
    }
}
