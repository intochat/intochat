using IntoChat.OS.Authority.Compute;
using IntoChat.OS.Contracts.Compute;

namespace IntoChat.OS.Authority.Tests.Integration;

public sealed class ComputeReservationFacts
{
    [Fact]
    public async Task LegacyChargeReplayThroughSettlementDoesNotChargeAgain()
    {
        var store = new ComputeStore<LedgerState>();
        var ledger = new LedgerGrain(store);
        var key = new ReservationKey("trace", "upgrade-retry", "brain");
        var cost = Cost(key, 125);
        Assert.True(await ledger.Charge(cost));
        Assert.False(await ledger.Settle(key, cost));
        Assert.Equal(-125, await ledger.Balance());
        Assert.Single(await ledger.Entries(10));
        await ledger.AcknowledgeSettlement(key);
        Assert.True(await ledger.Verify());
    }

    [Fact]
    public async Task ExternalSettlementCannotBorrowAnOldLegacyChargeIdentity()
    {
        var ledger = new LedgerGrain(new ComputeStore<LedgerState>());
        var key = new ReservationKey("shared-trace", "shared-cost", "other-brain");
        await ledger.Charge(Cost(key, 125));
        Assert.True(await ledger.Settle(key, Cost(key, 125) with { Kind = CostKind.ExternalProvider }));
        Assert.Equal(-250, await ledger.Balance());
    }

    [Fact]
    public async Task ReservationReplayAndPartialSettlementDoNotConsumeBudgetTwice()
    {
        var store = new ComputeStore<TraceBudgetState>();
        var budget = new TraceBudgetGrain(store);
        var first = new ReservationKey("trace", "first");
        Assert.True(await budget.TryReserve(first, 500, 1_000));
        Assert.True(await budget.TryReserve(first, 500, 1_000));
        Assert.False(await budget.TryReserve(new("trace", "blocked"), 501, 1_000));
        await budget.Settle(first, 200);
        await budget.Settle(first, 200);
        Assert.True(await budget.TryReserve(new("trace", "second"), 800, 1_000));
        Assert.False(await budget.TryReserve(new("trace", "third"), 1, 1_000));
        Assert.Equal(200, store.State.SpentMicros);
    }

    [Fact]
    public async Task ConflictingReservationOrSettlementCannotRewriteAnAcceptedAmount()
    {
        var budget = new TraceBudgetGrain(new ComputeStore<TraceBudgetState>());
        var key = new ReservationKey("trace", "attempt");
        Assert.True(await budget.TryReserve(key, 100, 1_000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => budget.TryReserve(key, 101, 1_000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => budget.Settle(key, 101));
        await budget.Release(key);
        await budget.Release(key);
        await Assert.ThrowsAsync<InvalidOperationException>(() => budget.Settle(key, 1));
    }

    [Fact]
    public async Task UnacknowledgedSettlementSurvivesTheRollingChargeWindow()
    {
        var store = new ComputeStore<LedgerState>();
        var ledger = new LedgerGrain(store);
        var key = new ReservationKey("trace", "lost-response");
        var cost = Cost(key, 500);
        Assert.True(await ledger.Settle(key, cost));
        for (var index = 0; index < 1_001; index++)
        {
            await ledger.Charge(Cost(new("other", index.ToString(System.Globalization.CultureInfo.InvariantCulture)), 1));
        }
        var reactivated = new LedgerGrain(store);
        Assert.False(await reactivated.Settle(key, cost));
        Assert.Equal(-1_501, await reactivated.Balance());
        Assert.True(await reactivated.Verify());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reactivated.Settle(key, cost with { Micros = 501 }));
    }

    [Fact]
    public async Task FailedSettlementWriteIsRetriedBeforeReturningDuplicateSuccess()
    {
        var store = new ComputeStore<LedgerState> { FailWrites = 1 };
        var ledger = new LedgerGrain(store);
        var key = new ReservationKey("trace", "retry-write");
        await Assert.ThrowsAsync<IOException>(() => ledger.Settle(key, Cost(key, 100)));
        Assert.False(await ledger.Settle(key, Cost(key, 100)));
        Assert.Equal(2, store.Writes);
        Assert.Equal(-100, await ledger.Balance());
    }

    internal static Cost Cost(ReservationKey key, long micros) => new()
    {
        TraceId = key.TraceId,
        CostId = key.CostId,
        Kind = CostKind.Model,
        Item = "test",
        Quantity = 1,
        Unit = "call",
        Micros = micros,
        At = DateTimeOffset.UtcNow,
    };
}

internal sealed class ComputeStore<T> : IPersistentState<T> where T : new()
{
    public T State { get; set; } = new();
    public string Etag => string.Empty;
    public bool RecordExists => true;
    public int FailWrites { get; set; }
    public int Writes { get; private set; }
    public Task ClearStateAsync() => Task.CompletedTask;
    public Task ReadStateAsync() => Task.CompletedTask;
    public Task WriteStateAsync()
    {
        Writes++;
        if (FailWrites > 0) { FailWrites--; throw new IOException("Injected accounting storage failure."); }
        return Task.CompletedTask;
    }
}
