namespace DigitalBrain.Platform.Parts;

// Own machines hold long leases and acknowledge once the journal is durable; circle and cloud machines hold
// short leases and acknowledge only after the batch is replicated (Q24, Q26).
public enum MachineRole
{
    Own = 0,
    Circle = 1,
    Cloud = 2,
}

public sealed class PartsOptions
{
    public const string Key = "DigitalBrain:Platform:Parts";

    public MachineRole Role { get; set; } = MachineRole.Own;

    public string MachineDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DigitalBrain", "machine");

    public TimeSpan OwnLease { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan ShortLease { get; set; } = TimeSpan.FromSeconds(30);

    // Short leases renew this long before they expire; own leases renew when less than half remains.
    public TimeSpan RenewBefore { get; set; } = TimeSpan.FromSeconds(10);

    public int BatchBytes { get; set; } = 262_144;

    // The largest single change a part log accepts: a batch always carries at least one change, so a change no commit
    // can carry would be retried forever while the journal grows behind it.
    public int MaxChangeBytes { get; set; } = 16 * 1024 * 1024;

    public TimeSpan BatchDelay { get; set; } = TimeSpan.FromMilliseconds(25);

    // Q17/T26-4: a holder that is not a circle part's preferred machine returns it after this long with no writes.
    public TimeSpan QuietFor { get; set; } = TimeSpan.FromSeconds(30);

    public int SnapshotEvery { get; set; } = 64;

    // Ledger 38: a snapshot is split into chunks no larger than this (serialized records/signals), so no single
    // mesh segment approaches DataShards x MaxShardBytes. 8 MiB here becomes ~1 MiB shards by default.
    public int SnapshotChunkBytes { get; set; } = 8 * 1024 * 1024;

    // Shared by every machine of one deployment (R52): a second machine joins an account only with it.
    public string? PairingSecret { get; set; }

    // R76/T20-11: boot acquisition only — the own machine that takes the owner's core at boot. Another own machine
    // never races for it at boot; its calls into the core (kernel grains included) are forwarded to the core's holder.
    public bool CoreHolder { get; set; }

    // R35/R78: Tpm (the Windows module) is the production default for a single-machine owner, and an own machine in
    // Production refuses Configured. The dev AppHost keeps Configured (one shared custody root, R74): multi-machine
    // owners under Tpm need part keys wrapped to each machine's TPM key (key sharing, later). Cloud may run Configured.
    // R81: a production own deployment sets Custody = Tpm (Windows + TPM) or ConfiguredCustodyAcknowledged = true,
    // which starts with a warning instead of refusing.
    // R79: switching custody replaces the signing key, so the machine id changes: the machine is a new machine that
    // re-registers (R52), and part keys wrapped under the previous custody stop unwrapping with the R74 error.
    public DigitalBrain.Framework.Parts.MasterKeyCustodyKind Custody { get; set; } = DigitalBrain.Framework.Parts.MasterKeyCustodyKind.Configured;

    // R81: the operator's explicit acceptance of a configured custody root on a production own machine.
    public bool ConfiguredCustodyAcknowledged { get; set; }

    public TimeSpan Lease => Role == MachineRole.Own ? OwnLease : ShortLease;

    public bool AcknowledgeOnJournal => Role == MachineRole.Own;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MachineDirectory)) { throw new InvalidOperationException("Parts needs a MachineDirectory."); }
        if (OwnLease <= TimeSpan.Zero || ShortLease <= TimeSpan.Zero || RenewBefore <= TimeSpan.Zero || BatchDelay <= TimeSpan.Zero)
        { throw new InvalidOperationException("Parts needs positive leases, renewal margin and batch delay."); }
        if (QuietFor <= TimeSpan.Zero) { throw new InvalidOperationException("Parts needs a positive QuietFor."); }
        if (BatchBytes < 1 || SnapshotEvery < 1 || MaxChangeBytes < 1) { throw new InvalidOperationException("Parts needs positive BatchBytes, MaxChangeBytes and SnapshotEvery."); }
        if (SnapshotChunkBytes < 1) { throw new InvalidOperationException("Parts needs a positive SnapshotChunkBytes."); }
        if (RenewBefore >= ShortLease) { throw new InvalidOperationException("Parts needs RenewBefore shorter than ShortLease."); }
    }
}
