using DigitalBrain.Framework.Compute;
using DigitalBrain.Platform.Compute;

namespace DigitalBrain.Platform.Tests.Unit;

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
        public Task ClearStateAsync() => Task.CompletedTask;
        public Task ReadStateAsync() => Task.CompletedTask;
        public Task WriteStateAsync() => Task.CompletedTask;
    }
}