using System.Security.Cryptography;
using IntoChat.OS.Authority.Keys;
using IntoChat.OS.Authority.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Tests.Integration.Storage;

public sealed class BrainLogOwnershipFacts(AuthorityHost host)
{
    private readonly FakeTimeProvider time = new();
    private readonly FakeLeaseClient client = new();

    [Fact]
    public async Task RenewedLeaseKeepsWritingPastTheLeaseDuration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var log = await Open(ct);
        for (var i = 0; i < 6; i++) { time.Advance(BlobLeaseBrainLogOwnership.RenewalInterval); }

        Assert.Equal(1, await log.Append("g", "state", null, BrainRecordChange.Write, [1], [], ct));
        Assert.Equal(6, client.Renewals);
    }

    [Fact]
    public async Task WritesAreRefusedOnceTheLastRenewalIsOlderThanTheFence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var log = await Open(ct);
        client.RenewFault = new IOException("storage unreachable");
        time.Advance(BlobLeaseBrainLogOwnership.Fence - TimeSpan.FromSeconds(1));
        Assert.Equal(1, await log.Append("g", "state", null, BrainRecordChange.Write, [1], [], ct));

        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<BrainNotOwnedException>(() => log.Append("g", "state", 1, BrainRecordChange.Write, [2], [], ct));
        await Assert.ThrowsAsync<BrainNotOwnedException>(() => log.Read("g", "state"));
        Assert.False(log.Usable);
        Assert.Equal(1, Ownership.Snapshot().Fenced);
    }

    [Fact]
    public async Task RefusedRenewalStopsWritesWithoutWaitingForTheFence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var log = await Open(ct);
        client.RenewAccepted = false;

        time.Advance(BlobLeaseBrainLogOwnership.RenewalInterval);

        await Assert.ThrowsAsync<BrainNotOwnedException>(() => log.Append("g", "state", null, BrainRecordChange.Write, [1], [], ct));
        await Assert.ThrowsAsync<BrainNotOwnedException>(() => log.Checkpoint(ct));
        client.RenewAccepted = true;
        time.Advance(BlobLeaseBrainLogOwnership.RenewalInterval);
        await Assert.ThrowsAsync<BrainNotOwnedException>(() => log.Append("g", "state", null, BrainRecordChange.Write, [1], [], ct));
    }

    [Fact]
    public async Task ClaimRetriesAHeldLeaseAndRefusesAfterTheClaimTimeout()
    {
        client.Holder = "other";
        var claim = Ownership.ClaimAsync("unused", "busy", TestContext.Current.CancellationToken);

        await AdvanceUntil(claim, TimeSpan.FromSeconds(60));

        await Assert.ThrowsAsync<BrainNotOwnedException>(() => claim);
        Assert.InRange(client.Acquires, 3, 20);
        Assert.Equal(TimeSpan.FromSeconds(25), time.GetUtcNow() - start);
        Assert.Equal(1, Ownership.Snapshot().Refused);
    }

    [Fact]
    public async Task ClaimSucceedsOnceThePreviousOwnerReleases()
    {
        client.Holder = "other";
        var claim = Ownership.ClaimAsync("unused", "busy", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(5));
        client.Holder = null;

        await AdvanceUntil(claim, TimeSpan.FromSeconds(30));

        await using var lease = await claim;
        Assert.True(lease.IsHeld);
    }

    [Fact]
    public async Task DisposeReleasesAHeldLeaseButNotALostOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var held = await Ownership.ClaimAsync("unused", "held", ct);
        await held.DisposeAsync();
        Assert.Equal(["held"], client.Released);

        var lost = await Ownership.ClaimAsync("unused", "lost", ct);
        client.RenewAccepted = false;
        time.Advance(BlobLeaseBrainLogOwnership.RenewalInterval);
        await lost.DisposeAsync();
        Assert.Equal(["held"], client.Released);
        Assert.Equal(new BrainOwnershipCounts("blob-lease", 2, 0, 1, 1, 0, 0), Ownership.Snapshot());
        Assert.Throws<ObjectDisposedException>(() => lost.BeginWrite());
    }

    [Fact]
    public async Task BrainLogsClaimThroughTheConfiguredOwnershipAndReleaseOnDispose()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Options.Create(new BrainStoreOptions { Directory = Directory.CreateTempSubdirectory("brain-lease-logs-").FullName });
        var keys = new BrainStorageKeys(new ConfiguredMasterKeyCustody(RandomNumberGenerator.GetBytes(32)), options);
        var logs = new BrainLogs(keys, host.Brain.Services.GetRequiredService<Serializer>(), options, ownership: Ownership);

        var log = await logs.For("alice").WaitAsync(ct);
        await log.Append("g", "state", null, BrainRecordChange.Write, [1], [], ct);
        await logs.DisposeAsync();

        Assert.Equal(["alice"], client.Released);
    }

    [Fact]
    public async Task AWriteLongerThanTheCapCountsAsLost()
    {
        await using var lease = await Ownership.ClaimAsync("unused", "slow", TestContext.Current.CancellationToken);
        var started = lease.BeginWrite();
        time.Advance(TimeSpan.FromSeconds(11));

        Assert.Throws<BrainNotOwnedException>(() => lease.EndWrite(started));
        Assert.False(lease.IsHeld);
        Assert.Equal(1, Ownership.Snapshot().Fenced);
    }

    [Fact]
    public async Task AFenceBetweenWriteAndFlushLeavesNothingToReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        var (directory, key, serializer) = NewBrain();
        var lease = new FailingLease(failOnEndWrite: 2);
        await using (var log = new BrainLog(directory, "owner", key, serializer, BrainLogFormat.Framed, lease))
        {
            await log.Append("g", "state", null, BrainRecordChange.Write, [1], [], ct);
            var length = new FileInfo(Journal(directory)).Length;

            await Assert.ThrowsAsync<BrainNotOwnedException>(() => log.Append("g", "state", 1, BrainRecordChange.Write, [2], [], ct));

            Assert.Equal(length, new FileInfo(Journal(directory)).Length);
            await Assert.ThrowsAsync<BrainNotOwnedException>(() => log.Append("g", "state", 1, BrainRecordChange.Write, [3], [], ct));
            Assert.False(log.Usable);
        }
        await using var recovered = new BrainLog(directory, "owner", key, serializer, BrainLogFormat.Framed);
        var record = (await recovered.Read("g", "state"))!;
        Assert.Equal([1], record.Record);
        Assert.Equal(1, record.ETag);
    }

    [Fact]
    public async Task ACheckpointCrossingTheFenceKeepsThePreviousGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var (directory, key, serializer) = NewBrain();
        await using (var log = new BrainLog(directory, "owner", key, serializer, BrainLogFormat.Framed, new FailingLease(failOnEndWrite: 2)))
        {
            await log.Append("g", "state", null, BrainRecordChange.Write, [1], [], ct);
            await Assert.ThrowsAsync<BrainNotOwnedException>(() => log.Checkpoint(ct));
        }
        Assert.False(File.Exists(Path.Combine(directory, "current")));
        Assert.Empty(Directory.EnumerateFiles(directory, "current*.tmp"));
        await using var recovered = new BrainLog(directory, "owner", key, serializer, BrainLogFormat.Framed);
        var record = (await recovered.Read("g", "state"))!;
        Assert.Equal([1], record.Record);
        Assert.Equal(1, record.ETag);
    }

    [Fact]
    public async Task BrainLogsReclaimABrainAfterItsLeaseIsLost()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Options.Create(new BrainStoreOptions { Directory = Directory.CreateTempSubdirectory("brain-lease-reclaim-").FullName });
        var keys = new BrainStorageKeys(new ConfiguredMasterKeyCustody(RandomNumberGenerator.GetBytes(32)), options);
        await using var logs = new BrainLogs(keys, host.Brain.Services.GetRequiredService<Serializer>(), options, ownership: Ownership);
        var first = await logs.For("alice").WaitAsync(ct);
        await first.Append("g", "state", null, BrainRecordChange.Write, [1], [], ct);
        client.RenewAccepted = false;
        time.Advance(BlobLeaseBrainLogOwnership.RenewalInterval);
        await Assert.ThrowsAsync<BrainNotOwnedException>(() => first.Append("g", "state", 1, BrainRecordChange.Write, [2], [], ct));

        client.RenewAccepted = true;
        var second = await logs.For("alice").WaitAsync(ct);

        Assert.NotSame(first, second);
        Assert.Equal(2, await second.Append("g", "state", 1, BrainRecordChange.Write, [2], [], ct));
        Assert.Equal(2, client.Acquires);
        Assert.Empty(client.Released);
    }

    private readonly DateTimeOffset start = new FakeTimeProvider().GetUtcNow();

    private BlobLeaseBrainLogOwnership Ownership => ownership ??=
        new(client, time, NullLogger<BlobLeaseBrainLogOwnership>.Instance, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(10));
    private BlobLeaseBrainLogOwnership? ownership;

    private (string Directory, byte[] Key, Serializer Serializer) NewBrain() =>
        (Directory.CreateTempSubdirectory("brain-fence").FullName, RandomNumberGenerator.GetBytes(32), host.Brain.Services.GetRequiredService<Serializer>());

    private static string Journal(string directory) => Assert.Single(Directory.EnumerateFiles(directory, "*.log"));

    // Loses ownership on the given EndWrite call, which lands between a journal write and its flush, or before a manifest move.
    private sealed class FailingLease(int failOnEndWrite) : IBrainLogLease
    {
        private int ends;
        private bool lost;
        public bool IsHeld => !lost;
        public long BeginWrite() => lost ? throw new BrainNotOwnedException("owner") : 0;
        public void EndWrite(long started)
        {
            if (++ends >= failOnEndWrite) { lost = true; }
            if (lost) { throw new BrainNotOwnedException("owner"); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task<BrainLog> Open(CancellationToken ct)
    {
        var lease = await Ownership.ClaimAsync("unused", "owner", ct);
        return new BrainLog(Directory.CreateTempSubdirectory("brain-lease").FullName, "owner", RandomNumberGenerator.GetBytes(32),
            host.Brain.Services.GetRequiredService<Serializer>(), BrainLogFormat.Legacy, lease);
    }

    // Steps the fake clock a second at a time, letting each delayed continuation run before the next step.
    private async Task AdvanceUntil(Task task, TimeSpan limit)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < limit && !task.IsCompleted; elapsed += TimeSpan.FromSeconds(1))
        {
            await WaitForPendingContinuations();
            if (task.IsCompleted) { break; }
            time.Advance(TimeSpan.FromSeconds(1));
        }
        await WaitForPendingContinuations();
        async Task WaitForPendingContinuations()
        {
            for (var i = 0; i < 50 && !task.IsCompleted; i++) { await Task.Delay(1, TestContext.Current.CancellationToken); }
        }
    }

    private sealed class FakeLeaseClient : IBrainLeaseClient
    {
        public string? Holder { get; set; }
        public bool RenewAccepted { get; set; } = true;
        public Exception? RenewFault { get; set; }
        public int Acquires { get; private set; }
        public int Renewals { get; private set; }
        public List<string> Released { get; } = [];

        public Task<string?> TryAcquireAsync(string brainId, TimeSpan duration, CancellationToken ct)
        {
            Acquires++;
            Assert.Equal(TimeSpan.FromSeconds(60), duration);
            return Task.FromResult(Holder is null ? Guid.NewGuid().ToString() : null);
        }

        public Task<bool> RenewAsync(string brainId, string leaseId, CancellationToken ct)
        {
            Renewals++;
            return RenewFault is { } fault ? Task.FromException<bool>(fault) : Task.FromResult(RenewAccepted);
        }

        public Task ReleaseAsync(string brainId, string leaseId, CancellationToken ct)
        {
            Released.Add(brainId);
            return Task.CompletedTask;
        }
    }
}
