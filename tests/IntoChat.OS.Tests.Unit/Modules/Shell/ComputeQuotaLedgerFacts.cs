using IntoChat.OS.Modules.Flutter.Compute;

namespace IntoChat.OS.Modules.Flutter;

public sealed class ComputeQuotaLedgerFacts
{
    [Fact]
    public void LedgerRecordsUsageAndAggregatesProperly()
    {
        var ledger = new ComputeQuotaLedger();
        Assert.Equal(0L, ledger.TotalSpentMicros);
        Assert.False(ledger.IsBudgetExhausted);

        ledger.Record("ino", 100_000L);
        ledger.Record("creator", 250_000L);
        ledger.Record("ino", 50_000L);

        Assert.Equal(400_000L, ledger.TotalSpentMicros);
        var rows = ledger.ToRows();
        Assert.Equal(2, rows.Length);
        Assert.Equal("creator", rows[0].Key);
        Assert.Equal(250_000L, rows[0].Micros);
        Assert.Equal(1L, rows[0].Count);

        Assert.Equal("ino", rows[1].Key);
        Assert.Equal(150_000L, rows[1].Micros);
        Assert.Equal(2L, rows[1].Count);
    }

    [Fact]
    public void BudgetExhaustionTriggersException()
    {
        var ledger = new ComputeQuotaLedger(monthlyBudgetMicros: 300_000L);
        ledger.Record("llm", 200_000L);
        Assert.False(ledger.IsBudgetExhausted);
        ledger.EnforceQuota();

        ledger.Record("llm", 150_000L);
        Assert.True(ledger.IsBudgetExhausted);
        Assert.True(ComputeQuotaLedger.CheckBudget(ledger.TotalSpentMicros, ledger.MonthlyBudgetMicros));

        var ex = Assert.Throws<ComputeQuotaExceededException>(ledger.EnforceQuota);
        Assert.Equal(350_000L, ex.SpentMicros);
        Assert.Equal(300_000L, ex.BudgetMicros);
    }
}
