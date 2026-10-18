using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Authority.Storage;

// The lease operations Blob ownership needs, so its timing rules are testable without a storage account.
internal interface IBrainLeaseClient
{
    // The lease id, or null when another writer holds the lease.
    Task<string?> TryAcquireAsync(string brainId, TimeSpan duration, CancellationToken ct);
    // False when the service refuses the renewal (the lease was lost or taken); throws on transport failures.
    Task<bool> RenewAsync(string brainId, string leaseId, CancellationToken ct);
    Task ReleaseAsync(string brainId, string leaseId, CancellationToken ct);
}

internal sealed class AzureBrainLeaseClient(BlobContainerClient container) : IBrainLeaseClient
{
    public async Task<string?> TryAcquireAsync(string brainId, TimeSpan duration, CancellationToken ct)
    {
        var blob = container.GetBlobClient(brainId);
        try { await blob.UploadAsync(BinaryData.Empty, overwrite: false, ct); }
        catch (RequestFailedException error) when (error.Status is 409 or 412) { /* Exists already, possibly leased by another writer. */ }
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
internal sealed class BlobLeaseBrainLogOwnership(IBrainLeaseClient client, TimeProvider time, ILogger<BlobLeaseBrainLogOwnership> logger,
    TimeSpan claimTimeout) : IBrainLogOwnership
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Fence = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);
    private readonly IBrainLeaseClient client = client;
    private readonly TimeProvider time = time;
    private readonly ILogger<BlobLeaseBrainLogOwnership> logger = logger;

    // Retries a held lease with backoff for up to the claim timeout, covering the previous owner's drain or lease expiry.
    public async Task<IBrainLogLease> ClaimAsync(string directory, string brainId, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            var leaseId = await client.TryAcquireAsync(brainId, LeaseDuration, ct);
            if (leaseId is not null)
            {
                BrainLogOwnershipMetrics.Record("acquired");
                logger.LogInformation("Brain log lease acquired for {BrainId}.", brainId);
                return new Lease(this, brainId, leaseId);
            }
            var remaining = claimTimeout - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                BrainLogOwnershipMetrics.Record("refused");
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
        private long renewed;
        private bool lost;
        private bool disposed;
        private Task renewal = Task.CompletedTask;

        public Lease(BlobLeaseBrainLogOwnership owner, string brainId, string leaseId)
        {
            this.owner = owner;
            this.brainId = brainId;
            this.leaseId = leaseId;
            renewed = owner.time.GetTimestamp();
            timer = owner.time.CreateTimer(_ => Renew(), null, RenewalInterval, RenewalInterval);
        }

        public void EnsureHeld()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!lost && owner.time.GetElapsedTime(renewed) >= Fence) { MarkLost("fence"); }
                if (lost) { throw new BrainNotOwnedException(brainId); }
            }
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
                if (accepted) { renewed = attempt; }
                else { MarkLost("renewal refused"); }
            }
        }

        private void MarkLost(string reason)
        {
            lost = true;
            BrainLogOwnershipMetrics.Record("lost");
            owner.logger.LogError("Brain log lease for {BrainId} lost ({Reason}); writes are refused.", brainId, reason);
        }

        public void Dispose()
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
                    owner.client.ReleaseAsync(brainId, leaseId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    BrainLogOwnershipMetrics.Record("released");
                    owner.logger.LogInformation("Brain log lease released for {BrainId}.", brainId);
                }
                catch (Exception error) { owner.logger.LogWarning(error, "Brain log lease release for {BrainId} failed; it expires on its own.", brainId); }
            }
            stopping.Dispose();
        }
    }
}
