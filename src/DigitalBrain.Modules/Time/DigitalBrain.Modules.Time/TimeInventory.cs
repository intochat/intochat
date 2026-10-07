using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Time;

[Alias("time.inventory")]
internal interface ITimeInventory : INeuron
{
    Task<TimeSchedule[]> Read();
    Task Track(TimeSchedule schedule);
    Task Remove(string typeId, string key);
}

[GenerateSerializer, Alias("time.schedule")]
internal sealed record TimeSchedule(
    [property: Id(0)] string TypeId,
    [property: Id(1)] string Key,
    [property: Id(2)] DateTimeOffset Due,
    [property: Id(3)] TimeSpan? Period);

[GenerateSerializer, Alias("time.inventory-state")]
internal sealed record TimeInventoryState
{
    [Id(0)] public TimeSchedule[] Schedules { get; init; } = [];
}

[GrainType("time.inventory")]
internal sealed class TimeInventory([PersistentState("schedules", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<TimeInventoryState> state)
    : Neuron, ITimeInventory
{
    public Task<TimeSchedule[]> Read() => Task.FromResult(state.State.Schedules.ToArray());
    public Task Track(TimeSchedule schedule) => Save([.. state.State.Schedules.Where(item => item.TypeId != schedule.TypeId || item.Key != schedule.Key), schedule]);
    public Task Remove(string typeId, string key) => Save(state.State.Schedules.Where(item => item.TypeId != typeId || item.Key != key).ToArray());
    private async Task Save(TimeSchedule[] schedules)
    {
        var previous = state.State;
        state.State = new() { Schedules = schedules };
        try { await state.WriteStateAsync(); }
        catch { state.State = previous; throw; }
    }
}

// Each schedule captures its own index reference in activation memory for callback cleanup.
// A missing caller is a trusted runtime/deployment operation, outside per-brain coverage.
internal sealed class TimeInventoryWriter(IGrainFactory grains, ILogger<TimeInventoryWriter> logger)
{
    public async Task<ITimeInventory?> Track(TimeSchedule schedule)
    {
        if (!CallerContextStamper.TryGet(out _)) { return null; }
        var index = grains.GetGrain<ITimeInventory>(BrainScope.CurrentId());
        try { await index.Track(schedule); }
        catch (Exception error) { logger.LogWarning(error, "Time schedule inventory write failed; scheduling continues"); }
        return index;
    }

    public async Task Remove(ITimeInventory? index, string typeId, string key)
    {
        if (index is null && CallerContextStamper.TryGet(out _)) { index = grains.GetGrain<ITimeInventory>(BrainScope.CurrentId()); }
        if (index is null) { return; }
        try { await index.Remove(typeId, key); }
        catch (Exception error) { logger.LogWarning(error, "Time schedule inventory cleanup failed; cancellation continues"); }
    }
}
