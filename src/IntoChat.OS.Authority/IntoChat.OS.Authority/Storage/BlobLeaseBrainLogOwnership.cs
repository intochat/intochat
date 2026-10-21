using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using IntoChat.OS.Framework.Diagnostics;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority.Storage;

// The lease operations Blob ownership needs, so its timing rules are testable without a storage account.
internal interface IBrainLeaseClient
{
    // The lease id, or null when another writer holds the lease.
    Task<string?> TryAcquireAsync(string brainId, TimeSpan duration, CancellationToken ct);
    // False when the service refuses the renewal (the lease was lost or taken); throws on transport failures.
    Task<bool> RenewAsync(string brainId, string leaseId, CancellationToken ct);
    Task ReleaseAsync(string brainId, string leaseId, CancellationToken ct);
}

// The lease container must already exist (provisioned with the environment); one empty blob per brain carries the lease.
internal sealed class AzureBrainLeaseClient(BlobContainerClient container, ILogger<AzureBrainLeaseClient> logger) : IBrainLeaseClient
{
    public async Task<string?> TryAcquireAsync(string brainId, TimeSpan duration, CancellationToken ct)
    {
        var blob = container.GetBlobClient(brainId);
        try { await blob.UploadAsync(BinaryData.Empty, overwrite: false, ct); }
        catch (RequestFailedException error) when (error.Status is 409 or 412) { logger.LogSwallowed(error, "lease blob exists already, possibly leased by another writer"); }
        try { return (await blob.GetBlobLeaseClient(Guid.NewGuid().ToString()).AcquireAsync(duration, cancellationToken: ct)).Value.LeaseId; }
        catch (RequestFailedException error) when (error.Status == 409) { return null; }
    }

    public async Task<bool> RenewAsync(string brainId, string leaseId, CancellationToken ct)
    {
        try { await container.GetBlobClient(brainId).GetBlobLeaseClient(leaseId).RenewAsync(cancellationToken: ct); return true; }
        catch (RequestFailedException error) when (error.Status is 404 or 409 or 412) { return false; }
    }

    public async Task ReleaseAsync(string brainId, string leaseId, CancellationToken ct)
        => await container.GetBlobClient(brainId).GetBlobLeaseClient(leaseId).ReleaseAsync(cancellationToken: ct);
}

// A 60 s Blob lease per brain, renewed every 20 s. Writes are refused once the last successful renewal is 40 s old on the
// monotonic clock, or at once when the service refuses a renewal, which leaves 20 s before another silo can acquire.
// A single write longer than the write cap also counts as lost, so a stalled write cannot run on into that margin unnoticed.
internal sealed class BlobLeaseBrainLogOwnership(IBrainLeaseClient client, TimeProvider time, ILogger<BlobLeaseBrainLogOwnership> logger,
    TimeSpan claimTimeout, TimeSpan writeTimeout) : IBrainLogOwnership, IBrainOwnershipCounters
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Fence = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(5);
    private readonly IBrainLeaseClient client = client;
    private readonly TimeProvider time = time;
    private readonly ILogger<BlobLeaseBrainLogOwnership> logger = logger;
    private readonly TimeSpan writeTimeout = writeTimeout;
    private long acquired, refused, lost, released, renewed, fenced;

    public BrainOwnershipCounts Snapshot() => new("blob-lease", Interlocked.Read(ref acquired), Interlocked.Read(ref refused),
        Interlocked.Read(ref lost), Interlocked.Read(ref released), Interlocked.Read(ref renewed), Interlocked.Read(ref fenced));

    // Retries a held lease with backoff for up to the claim timeout, which stays inside the Orleans call timeout.
    // Waiting out a longer handover (a previous owner's drain or lease expiry) is the caller's retry.
    public async Task<IBrainLogLease> ClaimAsync(string directory, string brainId, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            var leaseId = await client.TryAcquireAsync(brainId, LeaseDuration, ct);
            if (leaseId is not null)
            {
                Interlocked.Increment(ref acquired);
                logger.LogInformation("Brain log lease acquired for {BrainId}.", brainId);
                return new Lease(this, brainId, leaseId);
            }
            var remaining = claimTimeout - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                Interlocked.Increment(ref refused);
                logger.LogWarning("Brain log lease for {BrainId} is held by another writer.", brainId);
                throw new BrainNotOwnedException(brainId);
            }
            await Task.Delay(delay < remaining ? delay : remaining, time, ct);
            delay = delay * 2 < MaxBackoff ? delay * 2 : MaxBackoff;
        }
    }

    private sealed class Lease : IBrainLogLease
    {
        private readonly BlobLeaseBrainLogOwnership owner;
        private readonly string brainId;
        private readonly string leaseId;
        private readonly ITimer timer;
        private readonly CancellationTokenSource stopping = new();
        private readonly Lock gate = new();
        private long lastRenewal;
        private bool lost;
        private bool disposed;
        private Task renewal = Task.CompletedTask;

        public Lease(BlobLeaseBrainLogOwnership owner, string brainId, string leaseId)
        {
            this.owner = owner;
            this.brainId = brainId;
            this.leaseId = leaseId;
            lastRenewal = owner.time.GetTimestamp();
            timer = owner.time.CreateTimer(_ => Renew(), null, RenewalInterval, RenewalInterval);
        }

        public bool IsHeld { get { lock (gate) { return !disposed && Check(); } } }

        public long BeginWrite()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!Check()) { throw new BrainNotOwnedException(brainId); }
                return owner.time.GetTimestamp();
            }
        }

        public void EndWrite(long started)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!lost && owner.time.GetElapsedTime(started) > owner.writeTimeout) { MarkLost("write exceeded its time cap", fenced: true); }
                if (!Check()) { throw new BrainNotOwnedException(brainId); }
            }
        }

        // Applies the fence; caller holds the gate.
        private bool Check()
        {
            if (!lost && owner.time.GetElapsedTime(lastRenewal) >= Fence) { MarkLost("no renewal within the fence", fenced: true); }
            return !lost;
        }

        private void Renew()
        {
            lock (gate)
            {
                if (disposed || lost || !renewal.IsCompleted) { return; }
                renewal = RenewAsync();
            }
        }

        private async Task RenewAsync()
        {
            var attempt = owner.time.GetTimestamp();
            bool accepted;
            try { accepted = await owner.client.RenewAsync(brainId, leaseId, stopping.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                // A transport failure leaves the lease to the fence; the next tick retries.
                owner.logger.LogWarning(error, "Brain log lease renewal for {BrainId} failed.", brainId);
                return;
            }
            lock (gate)
            {
                if (disposed || lost) { return; }
                // The service extended the lease from no earlier than the attempt, so the fence counts from there.
                if (accepted) { lastRenewal = attempt; Interlocked.Increment(ref owner.renewed); }
                else { MarkLost("renewal refused", fenced: false); }
            }
        }

        private void MarkLost(string reason, bool fenced)
        {
            lost = true;
            if (fenced) { Interlocked.Increment(ref owner.fenced); } else { Interlocked.Increment(ref owner.lost); }
            owner.logger.LogError("Brain log lease for {BrainId} lost ({Reason}); writes are refused.", brainId, reason);
        }

        public async ValueTask DisposeAsync()
        {
            bool release;
            lock (gate)
            {
                if (disposed) { return; }
                disposed = true;
                release = !lost;
            }
            timer.Dispose();
            stopping.Cancel();
            if (release)
            {
                try
                {
                    await owner.client.ReleaseAsync(brainId, leaseId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                    Interlocked.Increment(ref owner.released);
                    owner.logger.LogInformation("Brain log lease released for {BrainId}.", brainId);
                }
                catch (Exception error) { owner.logger.LogWarning(error, "Brain log lease release for {BrainId} failed; it expires on its own.", brainId); }
            }
            stopping.Dispose();
        }
    }
}
