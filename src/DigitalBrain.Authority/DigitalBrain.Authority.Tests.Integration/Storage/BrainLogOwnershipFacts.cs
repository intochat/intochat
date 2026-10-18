using System.Security.Cryptography;
using DigitalBrain.Authority.Keys;
using DigitalBrain.Authority.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.Authority.Tests.Integration.Storage;

public sealed class BrainLogOwnershipFacts(AuthorityHost host)
{
    private readonly FakeTimeProvider time = new();
    private readonly FakeLeaseClient client = new();

    [Fact]
    public async Task RenewedLeaseKeepsWritingPastTheLeaseDuration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var log = await Open(ct);
        for (var i = 0; i < 6; i++) { time.Advance(BlobLeaseBrainLogOwnership.RenewalInterval); }

        Assert.Equal(1, await log.Append("g", "state", null, BrainRecordChange.Write, [1], [], ct));
        Assert.Equal(6, client.Renewals);
    }

    [Fact]
    public async Task WritesAreRefusedOnceTheLastRenewalIsOlderThanTheFence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var log = await Open(ct);
        client.RenewFault = new IOException("storage unreachable");
        time.Advance(BlobLeaseBrainLogOwnership.Fence - TimeSpan.FromSeconds(1));
        Assert.Equal(1, await log.Append("g", "state", null, BrainRecordChange.Write, [1], [], ct));

        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<BrainNotOwnedException>(() => log.Append("g", "state", 1, BrainRecordChange.Write, [2], [], ct));
        Assert.Equal([1], log.Read("g", "state")!.Record);
    }

    [Fact]
    public async Task RefusedRenewalStopsWritesWithoutWaitingForTheFence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var log = await Open(ct);
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
        var claim = Ownership().ClaimAsync("unused", "busy", TestContext.Current.CancellationToken);

        await AdvanceUntil(claim, TimeSpan.FromSeconds(120));

        await Assert.ThrowsAsync<BrainNotOwnedException>(() => claim);
        Assert.InRange(client.Acquires, 3, 20);
        Assert.Equal(TimeSpan.FromSeconds(90), time.GetUtcNow() - start);
    }

    [Fact]
    public async Task ClaimSucceedsOnceThePreviousOwnerReleases()
    {
        client.Holder = "other";
        var claim = Ownership().ClaimAsync("unused", "busy", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(5));
        client.Holder = null;

        await AdvanceUntil(claim, TimeSpan.FromSeconds(30));

        using var lease = await claim;
        lease.EnsureHeld();
    }

    [Fact]
    public async Task DisposeReleasesAHeldLeaseButNotALostOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var held = await Ownership().ClaimAsync("unused", "held", ct);
        held.Dispose();
        Assert.Equal(["held"], client.Released);

        var lost = await Ownership().ClaimAsync("unused", "lost", ct);
        client.RenewAccepted = false;
        time.Advance(BlobLeaseBrainLogOwnership.RenewalInterval);
        lost.Dispose();
        Assert.Equal(["held"], client.Released);
        Assert.Throws<ObjectDisposedException>(lost.EnsureHeld);
    }

    [Fact]
    public async Task BrainLogsClaimThroughTheConfiguredOwnershipAndReleaseOnDispose()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = Options.Create(new BrainStoreOptions { Directory = Directory.CreateTempSubdirectory("brain-lease-logs-").FullName });
        var keys = new BrainStorageKeys(new ConfiguredMasterKeyCustody(RandomNumberGenerator.GetBytes(32)), options);
        var logs = new BrainLogs(keys, host.Brain.Services.GetRequiredService<Serializer>(), options, ownership: Ownership());

        var log = await logs.For("alice").WaitAsync(ct);
        await log.Append("g", "state", null, BrainRecordChange.Write, [1], [], ct);
        await logs.DisposeAsync();

        Assert.Equal(["alice"], client.Released);
    }

    private readonly DateTimeOffset start = new FakeTimeProvider().GetUtcNow();

    private BlobLeaseBrainLogOwnership Ownership() =>
        new(client, time, NullLogger<BlobLeaseBrainLogOwnership>.Instance, TimeSpan.FromSeconds(90));

    private async Task<BrainLog> Open(CancellationToken ct)
    {
        var lease = await Ownership().ClaimAsync("unused", "owner", ct);
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
