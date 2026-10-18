namespace IntoChat.OS.Authority.Storage;

// How a silo proves it is the single writer of a brain's log. The claim is taken before the log touches the brain's key or files.
internal interface IBrainLogOwnership
{
    Task<IBrainLogLease> ClaimAsync(string directory, string brainId, CancellationToken ct);
}

// A held claim. Once it can no longer be trusted the log refuses to write and BrainLogs evicts it so the brain can be reclaimed.
internal interface IBrainLogLease : IDisposable
{
    // False once the claim is lost; never throws.
    bool IsHeld { get; }
    // Throws BrainNotOwnedException when the claim is lost. Returns a mark for EndWrite.
    long BeginWrite();
    // Throws BrainNotOwnedException when the claim was lost, or the write since BeginWrite took longer than the write cap.
    void EndWrite(long started);
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
        public bool IsHeld => true;
        public long BeginWrite() => 0;
        public void EndWrite(long started) { }
        public void Dispose() => stream.Dispose();
    }
}

// Reports the configured ownership's counters, or none for the writer lock.
internal sealed class BrainLogOwnershipCounters(IBrainLogOwnership ownership) : IBrainOwnershipCounters
{
    private static readonly BrainOwnershipCounts WriterLock = new("writer-lock", 0, 0, 0, 0, 0, 0);
    public BrainOwnershipCounts Snapshot() => ownership is IBrainOwnershipCounters counters ? counters.Snapshot() : WriterLock;
}
