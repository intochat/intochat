using DigitalBrain.Framework;

namespace DigitalBrain.Platform.Sequencer;

// Per part: the signed head with compare-and-set (Q10). A trust-cluster grain: it holds nothing it could misuse.
internal interface ISequencerGrain : IGrainWithStringKey
{
    Task<HeadRecord?> Get();

    Task<bool> CompareAndSet(long expectedVersion, HeadRecord next);
}

[GrainType("platform.sequencer")]
internal sealed class SequencerGrain(
    [PersistentState("sequencer", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<SequencerState> store,
    TimeProvider time) : Grain, ISequencerGrain
{
    public Task<HeadRecord?> Get() => Task.FromResult(store.State.Head);

    public async Task<bool> CompareAndSet(long expectedVersion, HeadRecord next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var current = store.State.Head;
        if ((current?.Version ?? 0) != expectedVersion || next.Version != expectedVersion + 1) { return false; }
        if (next.PartHash != this.GetPrimaryKeyString()) { return false; }
        if (!MachineKeys.Verify(next.HolderKey, next.SignedBytes(), next.Signature)) { return false; }
        if (next.Holder != MachineKeys.IdOf(next.HolderKey)) { return false; }
        if (current is not null && current.LeaseExpiry > time.GetUtcNow() && !current.HolderKey.AsSpan().SequenceEqual(next.HolderKey)) { return false; }
        store.State.Head = next;
        await store.WriteStateAsync();
        return true;
    }
}

[GenerateSerializer, Alias("platform.sequencer-state")]
internal sealed class SequencerState
{
    [Id(0)] public HeadRecord? Head { get; set; }
}
