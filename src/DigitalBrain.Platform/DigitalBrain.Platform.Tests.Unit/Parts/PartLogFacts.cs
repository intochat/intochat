using System.Security.Cryptography;
using DigitalBrain.Framework.Mesh;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class PartLogFacts
{
    internal static async Task<ITestBrain> StartAsync(CancellationToken ct, TimeProvider? time = null)
    {
        var builder = TestBrain.Create()
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.DataShards)}", "4")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ParityShards)}", "2");
        // The lease clock: a fact moves time past a lease without waiting for it.
        if (time is not null) { builder = builder.ConfigureServices(services => services.AddSingleton(time)); }
        var brain = await builder.StartAsync(ct);
        var directory = brain.Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key);
        for (var i = 0; i < 8; i++) { await directory.Register($"n{i}", "platform"); }
        return brain;
    }

    internal static async Task<PartLog> OpenAsync(ITestBrain brain, PartId part, byte[] key, MachineIdentity machine, PartsOptions options, TimeProvider time,
        CancellationToken ct, IMeshStore? mesh = null)
    {
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var lease = await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), machine, PartLeaseFacts.Seen(), options, time, serializer);
        return await PartLog.Open(part, key, lease, mesh ?? brain.SiloServices.GetRequiredService<IMeshStore>(), serializer, options, time,
            NullLogger<PartLog>.Instance, ct);
    }

    private static PartsOptions Cloud(TimeSpan? batchDelay = null) => new()
    {
        Role = MachineRole.Cloud, MachineDirectory = MachineDirectory(), BatchDelay = batchDelay ?? TimeSpan.FromMilliseconds(25),
    };

    private static PartsOptions Own() => new() { Role = MachineRole.Own, MachineDirectory = MachineDirectory() };

    private static string MachineDirectory() => Path.Combine(Path.GetTempPath(), $"machine-{Guid.NewGuid():N}");

    private static string JournalPath(PartsOptions options, PartId part) =>
        Path.Combine(options.MachineDirectory, "journal", $"{HeadRecord.HashOf(part.Value)}.log");

    [Fact(Timeout = 60_000)]
    public async Task AppendedChangesAreReadBackAfterReopening()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var options = Cloud();

        await using (var log = await OpenAsync(brain, part, key, machine, options, TimeProvider.System, ct))
        {
            await log.Append("g1", "s", RecordChange.Write, [1], []);
            await log.Append("g1", "s", RecordChange.Write, [2], []);
        }
        await using var reopened = await OpenAsync(brain, part, key, machine, options, TimeProvider.System, ct);

        var stored = reopened.Read("g1", "s");
        Assert.NotNull(stored);
        Assert.Equal([2], stored.Record);
        Assert.Equal(2, stored.ETagSeq);
    }

    [Fact(Timeout = 60_000)]
    public async Task TwoWritersExpectingTheSameVersionCannotBothAppend()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        using var machine = SequencerFacts.Machine();
        await using var log = await OpenAsync(brain, part, RandomNumberGenerator.GetBytes(32), machine, Cloud(TimeSpan.FromMilliseconds(200)), TimeProvider.System, ct);

        var first = log.AppendIfCurrent("g1", "s", null, RecordChange.Write, [1], []);
        var second = log.AppendIfCurrent("g1", "s", null, RecordChange.Write, [2], []);

        Assert.Equal(1, await first);
        await Assert.ThrowsAsync<Orleans.Storage.InconsistentStateException>(() => second);
        Assert.Equal(2, await log.AppendIfCurrent("g1", "s", 1, RecordChange.Write, [3], []));
    }

    [Fact(Timeout = 60_000)]
    public async Task ConcurrentAppendsShareOneSegment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var options = Cloud(TimeSpan.FromMilliseconds(50));
        var lease = await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), machine, PartLeaseFacts.Seen(), options, TimeProvider.System, serializer);
        await using var log = await PartLog.Open(part, key, lease, brain.SiloServices.GetRequiredService<IMeshStore>(), serializer, options,
            TimeProvider.System, NullLogger<PartLog>.Instance, ct);

        var seqs = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => log.Append($"g{i}", "s", RecordChange.Write, [(byte)i], [])));

        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), seqs.Order());
        Assert.Equal(1, lease.Payload.SegmentsSinceSnapshot);
        Assert.Equal(20, lease.Payload.LastSeq);
    }

    [Fact(Timeout = 60_000)]
    public async Task ASegmentWithoutAnAcceptedHeadIsInvisible()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var mesh = brain.SiloServices.GetRequiredService<IMeshStore>();
        var options = Cloud();
        var lease = await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), machine, PartLeaseFacts.Seen(), options, TimeProvider.System, serializer);
        await using (var log = await PartLog.Open(part, key, lease, mesh, serializer, options, TimeProvider.System, NullLogger<PartLog>.Instance, ct))
        {
            await log.Append("g1", "s", RecordChange.Write, [1], []);
            var orphan = new LogSegment
            {
                FirstSeq = 2, Prev = lease.Payload.Head,
                Entries = [new LogEntry { Seq = 2, GrainId = "g2", StateName = "s", Change = RecordChange.Write, Record = [2] }],
            };
            await mesh.Write(key, PartSegments.SegmentId(part, 2), serializer.SerializeToArray(orphan), ct);
        }

        await using var reopened = await OpenAsync(brain, part, key, machine, options, TimeProvider.System, ct);

        Assert.Equal(1, reopened.LastSeq);
        Assert.NotNull(reopened.Read("g1", "s"));
        Assert.Null(reopened.Read("g2", "s"));
    }

    [Fact(Timeout = 60_000)]
    public async Task AnOwnMachineKeepsWritingWhileTheSequencerIsUnreachableAndCommitsOnReconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var options = Own();
        var mesh = new UnreachableMesh(brain.SiloServices.GetRequiredService<IMeshStore>());

        await using (var log = await OpenAsync(brain, part, key, machine, options, TimeProvider.System, ct, mesh))
        {
            mesh.Offline = true;
            Assert.Equal(1, await log.Append("g1", "s", RecordChange.Write, [1], []));
            Assert.Equal(2, await log.Append("g2", "s", RecordChange.Write, [2], []));
            Assert.Equal([1], log.Read("g1", "s")!.Record);
            Assert.Equal([2], log.Read("g2", "s")!.Record);

            mesh.Offline = false;
            await log.Flushed();
        }
        await using var reopened = await OpenAsync(brain, part, key, machine, options, TimeProvider.System, ct);

        Assert.Equal([1], reopened.Read("g1", "s")!.Record);
        Assert.Equal([2], reopened.Read("g2", "s")!.Record);
        Assert.Equal(0, new FileInfo(JournalPath(options, part)).Length);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnOwnMachinesJournalThatConflictsIsSetAsideAndRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await StartAsync(ct, time);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var alice = SequencerFacts.Machine();
        using var bob = SequencerFacts.Machine();
        var aliceOptions = Own();
        var mesh = new UnreachableMesh(brain.SiloServices.GetRequiredService<IMeshStore>());
        await using (var a = await OpenAsync(brain, part, key, alice, aliceOptions, time, ct, mesh))
        {
            mesh.Offline = true;
            await a.Append("g1", "s", RecordChange.Write, [1], []);
        }

        time.Advance(aliceOptions.OwnLease + TimeSpan.FromMinutes(1));
        await using (var b = await OpenAsync(brain, part, key, bob, Own(), time, ct))
        {
            await b.Append("g1", "s", RecordChange.Write, [9], []);
            await b.Flushed();
        }

        await Assert.ThrowsAsync<JournalConflictException>(() => OpenAsync(brain, part, key, alice, aliceOptions, time, ct));
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(JournalPath(aliceOptions, part))!, "*.conflict-*"));
    }

    [Fact(Timeout = 60_000)]
    public async Task AWriteAfterTheLeaseIsLostFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await StartAsync(ct, time);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var alice = SequencerFacts.Machine();
        using var bob = SequencerFacts.Machine();
        var options = Cloud();
        await using var a = await OpenAsync(brain, part, key, alice, options, time, ct);
        var first = a.Append("g1", "s", RecordChange.Write, [1], []);
        await a.Flushed();
        await first;

        time.Advance(options.ShortLease + TimeSpan.FromSeconds(1));
        await using (var b = await OpenAsync(brain, part, key, bob, options, time, ct))
        {
            var lost = a.Append("g2", "s", RecordChange.Write, [2], []);
            await a.Flushed();
            await Assert.ThrowsAsync<LeaseLostException>(() => lost);
            await Assert.ThrowsAsync<LeaseLostException>(() => a.Append("g3", "s", RecordChange.Write, [3], []));
        }

        await using var reopened = await OpenAsync(brain, part, key, bob, options, time, ct);
        Assert.Equal(1, reopened.LastSeq);
        Assert.Equal([1], reopened.Read("g1", "s")!.Record);
        Assert.Null(reopened.Read("g2", "s"));
    }

    [Fact(Timeout = 60_000)]
    public async Task ACoveredJournalEntryAnotherMachineSnapshottedPastIsAConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await StartAsync(ct, time);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var alice = SequencerFacts.Machine();
        using var bob = SequencerFacts.Machine();
        var aliceOptions = Own();
        var mesh = new UnreachableMesh(brain.SiloServices.GetRequiredService<IMeshStore>());
        await using (var a = await OpenAsync(brain, part, key, alice, aliceOptions, time, ct, mesh))
        {
            mesh.Offline = true;
            await a.Append("g1", "s", RecordChange.Write, [1], []);
        }

        time.Advance(aliceOptions.OwnLease + TimeSpan.FromMinutes(1));
        var b = await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), bob, PartLeaseFacts.Seen(), Own(), time, brain.SiloServices.GetRequiredService<Serializer>());
        await b.Commit(new HeadPayload { SnapshotSeq = 1, LastSeq = 1 });
        await b.Release();

        await Assert.ThrowsAsync<JournalConflictException>(() => OpenAsync(brain, part, key, alice, aliceOptions, time, ct));
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(JournalPath(aliceOptions, part))!, "*.conflict-*"));
    }

    [Fact(Timeout = 60_000)]
    public async Task AnOwnMachineThatReacquiresAfterATakeoverAndFailsToOpenStillSeesTheConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await StartAsync(ct, time);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var alice = SequencerFacts.Machine();
        using var bob = SequencerFacts.Machine();
        var aliceOptions = Own();
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        var mesh = new UnreachableMesh(brain.SiloServices.GetRequiredService<IMeshStore>());
        await using (var a = await OpenAsync(brain, part, key, alice, aliceOptions, time, ct, mesh))
        {
            mesh.Offline = true;
            await a.Append("g1", "s", RecordChange.Write, [1], []);
        }

        time.Advance(aliceOptions.OwnLease + TimeSpan.FromMinutes(1));
        var bobOptions = Own();
        bobOptions.SnapshotEvery = 1;
        await using (var b = await OpenAsync(brain, part, key, bob, bobOptions, time, ct))
        {
            await b.Append("g1", "s", RecordChange.Write, [9], []);
            await b.Flushed();
        }
        // Alice takes the part back, then her open fails before it recovers the journal: she stays the holder.
        await PartLease.Acquire(part, key, PartLeaseFacts.Sequencer(brain), alice, PartLeaseFacts.Seen(), aliceOptions, time, serializer);

        await Assert.ThrowsAsync<JournalConflictException>(() => OpenAsync(brain, part, key, alice, aliceOptions, time, ct));
        var aside = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(JournalPath(aliceOptions, part))!, "*.conflict-*"));
        // The journal is sealed to its own path, so it is read back where it was written.
        File.Move(aside, JournalPath(aliceOptions, part));
        Assert.Equal([1], Assert.Single(new PartJournal(JournalPath(aliceOptions, part), key, serializer).Read()).Record);
    }

    [Fact(Timeout = 60_000)]
    public async Task AJournalEntryThisMachineCommittedButNeverTrimmedIsDroppedOnReopening()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var alice = SequencerFacts.Machine();
        var options = Own();
        options.SnapshotEvery = 1;
        var serializer = brain.SiloServices.GetRequiredService<Serializer>();
        await using (var a = await OpenAsync(brain, part, key, alice, options, TimeProvider.System, ct))
        {
            await a.Append("g1", "s", RecordChange.Write, [1], []);
            await a.Flushed();
        }
        // The silo died between the commit and the trim: the snapshot covers the entry, the journal still holds it.
        new PartJournal(JournalPath(options, part), key, serializer).Append(new LogEntry { Seq = 1, GrainId = "g1", StateName = "s", Change = RecordChange.Write, Record = [1] });

        await using var reopened = await OpenAsync(brain, part, key, alice, options, TimeProvider.System, ct);

        Assert.Equal([1], reopened.Read("g1", "s")!.Record);
        Assert.Empty(new PartJournal(JournalPath(options, part), key, serializer).Read());
    }

    [Fact(Timeout = 60_000)]
    public async Task AnAmbiguousCommitFailureClosesANonOwnLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var machine = SequencerFacts.Machine();
        var mesh = new UnreachableMesh(brain.SiloServices.GetRequiredService<IMeshStore>()) { FailAfterWrite = true };
        await using var log = await OpenAsync(brain, part, key, machine, Cloud(), TimeProvider.System, ct, mesh);

        var write = log.Append("g1", "s", RecordChange.Write, [1], []);
        await log.Flushed();

        await Assert.ThrowsAsync<TimeoutException>(() => write);
        var refused = log.Append("g2", "s", RecordChange.Write, [2], []);
        Assert.True(refused.IsFaulted);
        await Assert.ThrowsAsync<TimeoutException>(() => refused);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnOwnMachineWhoseOpenFailsTransientlyKeepsItsLease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var part = PartId.ForApp("home", "notes");
        var key = RandomNumberGenerator.GetBytes(32);
        using var alice = SequencerFacts.Machine();
        using var bob = SequencerFacts.Machine();
        var options = Own();
        var mesh = new UnreachableMesh(brain.SiloServices.GetRequiredService<IMeshStore>());
        await using (var a = await OpenAsync(brain, part, key, alice, options, TimeProvider.System, ct, mesh))
        {
            await a.Append("g1", "s", RecordChange.Write, [1], []);
            await a.Flushed();
            mesh.Offline = true;
            await a.Append("g2", "s", RecordChange.Write, [2], []);
        }

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => OpenAsync(brain, part, key, alice, options, TimeProvider.System, ct, mesh));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => OpenAsync(brain, part, key, bob, Own(), TimeProvider.System, ct));
    }

    // Fails at the mesh write, before the CAS, exactly as an unreachable platform would.
    private sealed class UnreachableMesh(IMeshStore inner) : IMeshStore
    {
        public volatile bool Offline;

        // The segment lands but the caller never hears back: whether the head moved is unknown to it.
        public volatile bool FailAfterWrite;

        private void Check()
        {
            if (Offline) { throw new ServiceUnavailableException("The platform is unreachable."); }
        }

        public Task<MeshRef> Write(byte[] key, string segmentId, byte[] plaintext, CancellationToken cancellationToken = default)
        {
            Check();
            if (!FailAfterWrite) { return inner.Write(key, segmentId, plaintext, cancellationToken); }
            return WriteThenTimeOut(key, segmentId, plaintext, cancellationToken);
        }

        private async Task<MeshRef> WriteThenTimeOut(byte[] key, string segmentId, byte[] plaintext, CancellationToken cancellationToken)
        {
            await inner.Write(key, segmentId, plaintext, cancellationToken);
            throw new TimeoutException("The platform did not answer.");
        }

        public Task<byte[]> Read(byte[] key, MeshRef reference, CancellationToken cancellationToken = default)
        { Check(); return inner.Read(key, reference, cancellationToken); }

        public Task<ShardAudit[]> Audit(byte[] key, MeshRef reference, int round, CancellationToken cancellationToken = default)
        { Check(); return inner.Audit(key, reference, round, cancellationToken); }

        public Task<MeshRepair> Repair(byte[] key, MeshRef reference, bool reissue = false, CancellationToken cancellationToken = default)
        { Check(); return inner.Repair(key, reference, reissue, cancellationToken); }

        public Task Discard(byte[] key, MeshRef reference, CancellationToken cancellationToken = default)
        { Check(); return inner.Discard(key, reference, cancellationToken); }
    }
}
