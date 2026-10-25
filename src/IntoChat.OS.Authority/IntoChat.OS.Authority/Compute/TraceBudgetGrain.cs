using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Authority.Compute;

[GrainType("trace-budget")]
internal sealed class TraceBudgetGrain(
    [PersistentState("trace-budget", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<TraceBudgetState> store)
    : Grain, ITraceBudgetGrain
{
    public Task<long> ReadSpent() => Task.FromResult(store.State.SpentMicros);

    public async Task<bool> TrySpend(long micros, long budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(micros);
        if (checked(store.State.SpentMicros + micros + Held()) > budget) { return false; }
        store.State.SpentMicros += micros;
        await store.WriteStateAsync();
        return true;
    }

    public async Task<bool> TryReserve(ReservationKey key, long maximumMicros, long budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumMicros);
        if (store.State.Reservations.FirstOrDefault(value => value.Key == key) is { } previous)
        {
            if (previous.MaximumMicros != maximumMicros) { throw new InvalidOperationException("Conflicting budget reservation."); }
            await store.WriteStateAsync();
            return true;
        }
        if (checked(store.State.SpentMicros + Held() + maximumMicros) > budget) { return false; }
        store.State.Reservations = [.. store.State.Reservations, new(key, maximumMicros)];
        await store.WriteStateAsync();
        return true;
    }

    public async Task Settle(ReservationKey key, long actualMicros)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actualMicros);
        var index = Array.FindIndex(store.State.Reservations, value => value.Key == key);
        if (index < 0) { throw new InvalidOperationException("Missing budget reservation."); }
        var reservation = store.State.Reservations[index];
        if (actualMicros > reservation.MaximumMicros || reservation.SettledMicros is { } settled && settled != actualMicros)
        { throw new InvalidOperationException("Conflicting budget settlement."); }
        if (reservation.SettledMicros is null)
        {
            store.State.SpentMicros = checked(store.State.SpentMicros + actualMicros);
            store.State.Reservations[index] = reservation with { SettledMicros = actualMicros };
        }
        await store.WriteStateAsync();
    }

    public Task Release(ReservationKey key) => Settle(key, 0);
    public async Task Acknowledge(ReservationKey key)
    {
        if (store.State.Reservations.Any(value => value.Key == key && value.SettledMicros is null)) { throw new InvalidOperationException("Cannot forget an unsettled budget hold."); }
        store.State.Reservations = [.. store.State.Reservations.Where(value => value.Key != key)];
        await store.WriteStateAsync();
    }
    public async Task Expire()
    {
        if (store.State.Reservations.Length != 0) { throw new InvalidOperationException("Cannot expire unacknowledged budget reservations."); }
        await store.ClearStateAsync();
    }
    private long Held() => store.State.Reservations.Where(value => value.SettledMicros is null).Sum(value => value.MaximumMicros);
}

[GenerateSerializer, Alias("platform.trace-budget-state")]
internal sealed class TraceBudgetState
{
    [Id(0)] public long SpentMicros { get; set; }
    [Id(1)] public FundingReservation[] Reservations { get; set; } = [];
}
