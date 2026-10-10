using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Parts;

// This machine's hold on one part (Q11, Q15, Q26). Every head change is a signed CAS at the sequencer; once a CAS
// is refused the lease is lost for good, so nothing written after it can be reported durable.
internal sealed class PartLease
{
    private readonly PartId _part;
    private readonly byte[] _key;
    private readonly ISequencerClient _sequencer;
    private readonly MachineIdentity _machine;
    private readonly SeenVersions _seen;
    private readonly PartsOptions _options;
    private readonly TimeProvider _time;
    private readonly Serializer _serializer;
    private DateTimeOffset _expiry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _lost;

    private PartLease(PartId part, byte[] key, ISequencerClient sequencer, MachineIdentity machine, SeenVersions seen,
        PartsOptions options, TimeProvider time, Serializer serializer) =>
        (_part, _key, _sequencer, _machine, _seen, _options, _time, _serializer) = (part, key, sequencer, machine, seen, options, time, serializer);

    public HeadPayload Payload { get; private set; } = new();

    public long Version { get; private set; }

    public string MachineId => _machine.MachineId;

    public bool NeedsRenewal => _expiry - _time.GetUtcNow() < (_options.Role == MachineRole.Own ? _options.OwnLease / 2 : _options.RenewBefore);

    // Past its expiry the part is anyone's to take; only the next CAS can tell whether this machine still holds it.
    public bool Expired => _expiry <= _time.GetUtcNow();

    private string Hash => HeadRecord.HashOf(_part.Value);

    private string Context => $"part-head|{_part.Value}";

    public static async Task<PartLease> Acquire(PartId part, byte[] key, ISequencerClient sequencer, MachineIdentity machine, SeenVersions seen,
        PartsOptions options, TimeProvider time, Serializer serializer)
    {
        if (part.IsCore && options.Role != MachineRole.Own) { throw new UnauthorizedAccessException($"The core of {part.BrainId} is held only by its owner's machines."); }
        var lease = new PartLease(part, key, sequencer, machine, seen, options, time, serializer);
        if (await lease._sequencer.Get(lease.Hash) is { } current)
        {
            seen.Check(lease.Hash, current.Version);
            if (current.Holder != machine.MachineId && current.LeaseExpiry > time.GetUtcNow())
            { throw new ServiceUnavailableException($"Part {part} is held by machine {current.Holder} until {current.LeaseExpiry:O}."); }
            lease.Version = current.Version;
            lease.Payload = serializer.Deserialize<HeadPayload>(SegmentSeal.Open(key, lease.Context, current.Payload))!;
        }
        await lease.Commit(lease.Payload);
        return lease;
    }

    public async Task Commit(HeadPayload next) => await Cas(next, _options.Lease);

    public async Task Renew() => await Cas(null, _options.Lease);

    public async Task Release()
    {
        if (_lost) { return; }
        try { await Cas(null, TimeSpan.Zero); }
        finally { _lost = true; }
    }

    // Serialized: overlapping CASes would expect the same version and the loser would lose the lease for good.
    // A null payload keeps the current one; a lease length is measured from the moment the CAS is built.
    private async Task Cas(HeadPayload? next, TimeSpan length)
    {
        await _gate.WaitAsync();
        try
        {
            if (_lost) { throw new LeaseLostException($"The lease on part {_part} is lost."); }
            next ??= Payload;
            var expiry = _time.GetUtcNow() + length;
            var record = new HeadRecord
            {
                PartHash = Hash, Version = Version + 1, Holder = _machine.MachineId, HolderKey = _machine.PublicKey, LeaseExpiry = expiry,
                Payload = SegmentSeal.Close(_key, Context, _serializer.SerializeToArray(next)),
            };
            record.Signature = _machine.Sign(record.SignedBytes());
            if (!await _sequencer.CompareAndSet(Hash, Version, record))
            {
                _lost = true;
                throw new LeaseLostException($"The sequencer refused version {Version + 1} of part {_part}.");
            }
            (Version, Payload, _expiry) = (record.Version, next, expiry);
            _seen.Saw(Hash, Version);
        }
        finally { _gate.Release(); }
    }
}
