using DigitalBrain.Framework;

namespace DigitalBrain.Platform.Compute;

// Per circle key: what share of its owner's budget the circle's members may spend, and what they
// have drawn. A plain grain, not a neuron, so no script can address it.
internal interface ICircleShareGrain : IGrainWithStringKey
{
    Task Configure(string ownerAccountId, int percent);
    Task<string?> Owner();
    // Draws when the total drawn stays within percent of (ownerFunds + already drawn).
    Task<bool> TryDraw(long micros, long ownerFunds);
}

[GrainType("circle-share")]
internal sealed class CircleShareGrain(
    [PersistentState("circle-share", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<CircleShareState> store)
    : Grain, ICircleShareGrain
{
    public async Task Configure(string ownerAccountId, int percent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerAccountId);
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        store.State.OwnerAccountId = ownerAccountId;
        store.State.Percent = percent;
        await store.WriteStateAsync();
    }

    public Task<string?> Owner() => Task.FromResult(store.State.OwnerAccountId);

    public async Task<bool> TryDraw(long micros, long ownerFunds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(micros);
        var cap = Math.Max(0, ownerFunds + store.State.SpentMicros) * store.State.Percent / 100;
        if (store.State.SpentMicros + micros > cap) { return false; }
        store.State.SpentMicros += micros;
        await store.WriteStateAsync();
        return true;
    }
}

internal sealed class CircleShares(IGrainFactory grains) : DigitalBrain.Framework.Compute.ICircleShares
{
    public Task Configure(string circleKey, string ownerAccountId, int percent)
        => grains.GetGrain<ICircleShareGrain>(circleKey).Configure(ownerAccountId, percent);
}

[GenerateSerializer, Alias("platform.circle-share-state")]
internal sealed class CircleShareState
{
    [Id(0)] public string? OwnerAccountId { get; set; }
    [Id(1)] public int Percent { get; set; }
    [Id(2)] public long SpentMicros { get; set; }
}
