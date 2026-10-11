using DigitalBrain.Framework;

namespace DigitalBrain.Platform.Sequencer;

// Per part: the signed head with compare-and-set (Q10). A trust-cluster grain: it holds nothing it could misuse.
internal interface ISequencerGrain : IGrainWithStringKey
{
    Task<HeadRecord?> Get();

    Task<bool> CompareAndSet(long expectedVersion, HeadRecord next);

    // R153: the holder's lease ends now when it is `machineId`; its next CAS is refused, so a withdrawn circle machine
    // cannot write until expiry. The head reads as expired, so another machine takes the part at once.
    Task Revoke(string machineId);
}

[GrainType("platform.sequencer")]
internal sealed class SequencerGrain(
    [PersistentState("sequencer", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<SequencerState> store,
    TimeProvider time) : Grain, ISequencerGrain
{
    public Task<HeadRecord?> Get() => Task.FromResult(store.State is { Head: { } head, Revoked: { } revoked } && head.Holder == revoked
        ? new HeadRecord
        {
            PartHash = head.PartHash, Version = head.Version, Holder = head.Holder, HolderKey = head.HolderKey,
            LeaseExpiry = DateTimeOffset.MinValue, Payload = head.Payload, Signature = head.Signature,
        }
        : store.State.Head);

    public async Task Revoke(string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        if (store.State.Head?.Holder != machineId || store.State.Revoked == machineId) { return; }
        store.State.Revoked = machineId;
        await store.WriteStateAsync();
    }

    public async Task<bool> CompareAndSet(long expectedVersion, HeadRecord next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var current = store.State.Head;
        if ((current?.Version ?? 0) != expectedVersion || next.Version != expectedVersion + 1) { return false; }
        if (next.PartHash != this.GetPrimaryKeyString()) { return false; }
        if (!MachineKeys.Verify(next.HolderKey, next.SignedBytes(), next.Signature)) { return false; }
        if (next.Holder != MachineKeys.IdOf(next.HolderKey)) { return false; }
        if (store.State.Revoked == next.Holder) { return false; }
        var revoked = current is not null && store.State.Revoked == current.Holder;
        if (current is not null && !revoked && current.LeaseExpiry > time.GetUtcNow() && !current.HolderKey.AsSpan().SequenceEqual(next.HolderKey)) { return false; }
        store.State.Head = next;
        store.State.Revoked = null;
        await store.WriteStateAsync();
        return true;
    }
}

[GenerateSerializer, Alias("platform.sequencer-state")]
internal sealed class SequencerState
{
    [Id(0)] public HeadRecord? Head { get; set; }
    [Id(1)] public string? Revoked { get; set; }
}
