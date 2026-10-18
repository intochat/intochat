using DigitalBrain.Framework.Compute;
using DigitalBrain.Authority.Compute;

namespace DigitalBrain.Authority.Tests.Integration;

public sealed class LedgerFacts
{
    private const string Account = "acct-7";

    [Fact]
    public async Task ChargingTheSameCostTwiceChargesOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var ledger = brain.Grains.GetGrain<ILedgerGrain>(Account);

        Assert.True(await ledger.Charge(Cost("tr-1", "c-1", 1_000L)));
        Assert.False(await ledger.Charge(Cost("tr-1", "c-1", 1_000L)));

        Assert.Equal(-1_000L, await ledger.Balance());
        var charge = Assert.Single(await ledger.Entries(10));
        Assert.Equal("tr-1/c-1", charge.EntryId);
        Assert.Equal(-1_000L, charge.Micros);
        Assert.True(await ledger.Verify());
    }

    [Fact]
    public async Task CreditThenChargeLeavesTheDifference()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var ledger = brain.Grains.GetGrain<ILedgerGrain>(Account);

        await ledger.Credit("grant-1", 5_000L, "monthly grant");
        await ledger.Charge(Cost("tr-1", "c-1", 1_000L));

        Assert.Equal(4_000L, await ledger.Balance());
        var entries = await ledger.Entries(10);
        Assert.Equal(2, entries.Length);
        Assert.Equal("tr-1/c-1", entries[0].EntryId);
        Assert.Equal("grant-1", entries[1].EntryId);
        Assert.True(await ledger.Verify());
    }

    [Fact]
    public async Task ACreditWithTheSameEntryIdIsAppliedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var ledger = brain.Grains.GetGrain<ILedgerGrain>(Account);

        Assert.True(await ledger.Credit("audit/n/s/0", 5L, "storage audit"));
        Assert.False(await ledger.Credit("audit/n/s/0", 5L, "storage audit"));

        Assert.Equal(5L, await ledger.Balance());
        Assert.Single(await ledger.Entries(10));
        Assert.True(await ledger.Verify());
    }

    // R141: credits keep their own applied-id window, so a busy account's charges cannot make a credit apply twice.
    [Fact]
    public async Task ChargeVolumeDoesNotEvictACreditId()
    {
        var ledger = new LedgerGrain(new ScriptedLedgerStore());
        Assert.True(await ledger.Credit("audit/n/s/0", 5L, "storage audit"));

        for (var i = 0; i < 10_001; i++) { await ledger.Charge(Cost("tr-1", $"c-{i}", 1L)); }

        Assert.False(await ledger.Credit("audit/n/s/0", 5L, "storage audit"));
        Assert.Equal(5L - 10_001L, await ledger.Balance());
    }

    // Every metered call of an account charges this one grain: charges arriving while a write is out share the next one.
    [Fact]
    public async Task ABurstOfChargesCostsAtMostTwoWrites()
    {
        var store = new ScriptedLedgerStore { Held = new TaskCompletionSource() };
        var ledger = new LedgerGrain(store);

        var charges = Enumerable.Range(0, 50).Select(i => ledger.Charge(Cost("tr-1", $"c-{i}", 1L))).ToArray();
        store.Held.SetResult();

        Assert.All(await Task.WhenAll(charges), Assert.True);
        Assert.InRange(store.Writes, 1, 2);
        Assert.Equal(-50L, await ledger.Balance());
        Assert.Equal(50, (await ledger.Entries(100)).Length);
        Assert.True(await ledger.Verify());
    }

    [Fact]
    public async Task AChargeWriteAfterTenThousandChargesPersistsABoundedPayload()
    {
        var store = new ScriptedLedgerStore();
        var ledger = new LedgerGrain(store);

        for (var i = 0; i < 10_000; i++) { await ledger.Charge(Cost("tr-1", $"c-{i}", 3L)); }

        Assert.Equal(LedgerGrain.EntryWindow, store.State.Entries.Length);
        Assert.True(store.State.RecentKeys.Length <= 1_000);
        Assert.True(System.Text.Json.JsonSerializer.Serialize(store.State).Length < 400_000);
    }

    [Fact]
    public async Task TotalsSurviveTheHistoryBeingBounded()
    {
        var store = new ScriptedLedgerStore();
        var ledger = new LedgerGrain(store);
        await ledger.Credit("grant-1", 50_000L, "grant");

        for (var i = 0; i < 2_000; i++) { await ledger.Charge(Cost("tr-1", $"c-{i}", 3L)); }

        Assert.Equal(50_000L - 6_000L, await ledger.Balance());
        Assert.True(await ledger.Verify());
        Assert.Equal("c-1999", (await ledger.Entries(1))[0].EntryId.Split('/')[1]);
        Assert.False(await ledger.Charge(Cost("tr-1", "c-1999", 3L)));
    }

    [Fact]
    public async Task ARewrittenEntryFailsVerification()
    {
        var store = new ScriptedLedgerStore();
        var ledger = new LedgerGrain(store);
        await ledger.Charge(Cost("tr-1", "c-1", 1_000L));

        Assert.True(await ledger.Verify());
        var rewritten = store.State.Entries[0] with { Micros = -42_000 };
        store.State.Entries = [rewritten];
        Assert.False(await ledger.Verify());
    }

    [Fact]
    public async Task BalanceSurvivesReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().StartAsync(ct);
        var ledger = brain.Grains.GetGrain<ILedgerGrain>(Account);
        await ledger.Credit("grant-1", 5_000L, "monthly grant");
        await ledger.Charge(Cost("tr-1", "c-1", 1_000L));

        await brain.DeactivateAsync(ledger, ct);

        Assert.Equal(4_000L, await ledger.Balance());
        Assert.True(await ledger.Verify());
    }

    private static Cost Cost(string traceId, string costId, long micros) => new()
    {
        TraceId = traceId,
        CostId = costId,
        Kind = CostKind.Model,
        Item = "gpt-5.1:input",
        Quantity = 1m,
        Unit = "mtok",
        Micros = micros,
        At = DateTimeOffset.UtcNow,
    };

    private sealed class ScriptedLedgerStore : IPersistentState<LedgerState>
    {
        public LedgerState State { get; set; } = new();
        public string Etag => string.Empty;
        public bool RecordExists => true;
        // The first write stays out until this completes.
        public TaskCompletionSource? Held { get; init; }
        public int Writes { get; private set; }
        public Task ClearStateAsync() => Task.CompletedTask;
        public Task ReadStateAsync() => Task.CompletedTask;
        public Task WriteStateAsync() => ++Writes == 1 && Held is { } held ? held.Task : Task.CompletedTask;
    }
}
