using IntoChat.OS.Framework;

namespace IntoChat.OS.Authority.Compute;

[GrainType("trace-budget")]
internal sealed class TraceBudgetGrain(
    [PersistentState("trace-budget", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<TraceBudgetState> store)
    : Grain, ITraceBudgetGrain
{
    public async Task<bool> TrySpend(long micros, long budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(micros);
        if (store.State.SpentMicros + micros > budget) { return false; }
        store.State.SpentMicros += micros;
        await store.WriteStateAsync();
        return true;
    }
}

[GenerateSerializer, Alias("platform.trace-budget-state")]
internal sealed class TraceBudgetState
{
    [Id(0)] public long SpentMicros { get; set; }
}
