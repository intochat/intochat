using System.Diagnostics.Metrics;

namespace DigitalBrain.Authority.Storage;

// How a silo proves it is the single writer of a brain's log. The claim is taken before the log touches the brain's key or files.
internal interface IBrainLogOwnership
{
    Task<IBrainLogLease> ClaimAsync(string directory, string brainId, CancellationToken ct);
}

// A held claim. EnsureHeld throws once the claim can no longer be trusted; the log then refuses to write.
internal interface IBrainLogLease : IDisposable
{
    void EnsureHeld();
}

// The default: an exclusive writer.lock in the brain's directory, held for the life of the log.
internal sealed class FileLockBrainLogOwnership : IBrainLogOwnership
{
    public static FileLockBrainLogOwnership Instance { get; } = new();

    public Task<IBrainLogLease> ClaimAsync(string directory, string brainId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Claim(directory, brainId));
    }

    // A second writer, in this or another process, is refused.
    public static IBrainLogLease Claim(string directory, string brainId)
    {
        Directory.CreateDirectory(directory);
        try { return new FileLease(new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException error) when (error is not FileNotFoundException and not DirectoryNotFoundException)
        { throw new BrainNotOwnedException(brainId, error); }
    }

    private sealed class FileLease(FileStream stream) : IBrainLogLease
    {
        public void EnsureHeld() { }
        public void Dispose() => stream.Dispose();
    }
}

internal static class BrainLogOwnershipMetrics
{
    public const string MeterName = "DigitalBrain.Authority.BrainLog";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("brainlog.ownership",
        description: "Brain log ownership events: acquired, refused, lost, released.");

    public static void Record(string outcome) => Events.Add(1, new KeyValuePair<string, object?>("event", outcome));
}
