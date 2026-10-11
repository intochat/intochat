using DigitalBrain.Framework;

namespace DigitalBrain.Platform.Keys;

// Wrapped part keys of one brain, in the trust cluster: ciphertext only, so holding it teaches us nothing. An owner's
// part key is one wrap under the owner's custody (R74); a circle part key is one wrap per contributed machine, per
// generation (T26-5): a rotation starts the next generation, and older generations stay as they were (R131).
internal interface IKeyringGrain : IGrainWithStringKey
{
    Task<byte[]?> Get(string partId);

    Task<bool> TryAdd(string partId, byte[] wrapped);

    // The machine's wrap of the part's current generation; null when it has none.
    [Orleans.Concurrency.AlwaysInterleave]
    Task<CircleKeyWrap?> Wrap(string partId, string machineId);

    // Every wrap the machine holds of the part, newest generation first: what reopens a head sealed before a rotation.
    [Orleans.Concurrency.AlwaysInterleave]
    Task<CircleKeyWrap[]> WrapsOf(string partId, string machineId);

    // The part's current generation; 0 before its first wraps.
    [Orleans.Concurrency.AlwaysInterleave]
    Task<long> Generation(string partId);

    // The circle parts this keyring holds wraps of.
    [Orleans.Concurrency.AlwaysInterleave]
    Task<string[]> CircleParts();

    // Wraps of the current generation join it (a contributed machine); wraps of the next one start it (a rotation).
    // False when `generation` is neither: another machine rotated meanwhile.
    Task<bool> AddWraps(string partId, long generation, CircleKeyWrap[] wraps);
}

// One circle part key wrapped to one machine's agreement key (R127): an ephemeral-static ECDH wrap, ciphertext only.
[GenerateSerializer, Alias("platform.circle-key-wrap")]
internal sealed record CircleKeyWrap(
    [property: Id(0)] string PartId,
    [property: Id(1)] long Generation,
    [property: Id(2)] string MachineId,
    [property: Id(3)] byte[] Wrapped);

[GrainType("platform.keyring")]
internal sealed class KeyringGrain(
    [PersistentState("keyring", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<KeyringState> store) : Grain, IKeyringGrain
{
    public Task<byte[]?> Get(string partId) => Task.FromResult(store.State.Keys.GetValueOrDefault(partId));

    public async Task<bool> TryAdd(string partId, byte[] wrapped)
    {
        if (store.State.Keys.ContainsKey(partId)) { return false; }
        store.State.Keys[partId] = wrapped;
        await store.WriteStateAsync();
        return true;
    }

    public Task<CircleKeyWrap?> Wrap(string partId, string machineId)
    {
        var current = Current(partId);
        return Task.FromResult(store.State.Wraps.FirstOrDefault(wrap => wrap.PartId == partId && wrap.Generation == current && wrap.MachineId == machineId));
    }

    public Task<CircleKeyWrap[]> WrapsOf(string partId, string machineId)
        => Task.FromResult<CircleKeyWrap[]>([.. store.State.Wraps.Where(wrap => wrap.PartId == partId && wrap.MachineId == machineId).OrderByDescending(wrap => wrap.Generation)]);

    public Task<long> Generation(string partId) => Task.FromResult(Current(partId));

    public Task<string[]> CircleParts() => Task.FromResult<string[]>([.. store.State.Wraps.Select(wrap => wrap.PartId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

    public async Task<bool> AddWraps(string partId, long generation, CircleKeyWrap[] wraps)
    {
        ArgumentNullException.ThrowIfNull(wraps);
        var current = Current(partId);
        if (generation != current && generation != current + 1) { return false; }
        if (wraps.Any(wrap => wrap.PartId != partId || wrap.Generation != generation))
        { throw new ArgumentException($"Every wrap names part {partId} at generation {generation}.", nameof(wraps)); }
        var fresh = wraps.Where(wrap => !store.State.Wraps.Any(known => known.PartId == partId && known.Generation == generation && known.MachineId == wrap.MachineId));
        store.State.Wraps = [.. store.State.Wraps, .. fresh];
        await store.WriteStateAsync();
        return true;
    }

    private long Current(string partId)
        => store.State.Wraps.Where(wrap => wrap.PartId == partId).Select(wrap => wrap.Generation).DefaultIfEmpty(0).Max();
}

[GenerateSerializer, Alias("platform.keyring-state")]
internal sealed class KeyringState
{
    [Id(0)] public Dictionary<string, byte[]> Keys { get; set; } = new(StringComparer.Ordinal);
    [Id(1)] public CircleKeyWrap[] Wraps { get; set; } = [];
}
