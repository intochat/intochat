using System.Collections.Concurrent;

namespace IntoChat.OS.Modules.Flutter.Compute;

/// <summary>
/// Metering ledger and quota governor tracking execution cost, turn counts, and budget limits per brain.
/// </summary>
public sealed class ComputeQuotaLedger
{
    private readonly ConcurrentDictionary<string, (long Micros, long Count)> _entries = new(StringComparer.Ordinal);
    private long _monthlyBudgetMicros = ComputeState.DefaultMonthlyBudgetMicros;

    public ComputeQuotaLedger() : this([]) { }

    public ComputeQuotaLedger(IEnumerable<ComputeRow> initialRows, long monthlyBudgetMicros = ComputeState.DefaultMonthlyBudgetMicros)
    {
        _monthlyBudgetMicros = monthlyBudgetMicros > 0 ? monthlyBudgetMicros : ComputeState.DefaultMonthlyBudgetMicros;
        if (initialRows is not null)
        {
            foreach (var row in initialRows)
            {
                if (!string.IsNullOrWhiteSpace(row.Key))
                {
                    _entries[row.Key] = (row.Micros, row.Count);
                }
            }
        }
    }

    public long MonthlyBudgetMicros
    {
        get => _monthlyBudgetMicros;
        set => _monthlyBudgetMicros = value > 0 ? value : ComputeState.DefaultMonthlyBudgetMicros;
    }

    public long TotalSpentMicros => _entries.Values.Sum(static e => e.Micros);

    public bool IsBudgetExhausted => TotalSpentMicros > _monthlyBudgetMicros;

    public void Record(string key, long micros, long count = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(micros);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        _entries.AddOrUpdate(
            key,
            _ => (micros, count),
            (_, existing) => (existing.Micros + micros, existing.Count + count));
    }

    public ComputeRow[] ToRows(int limit = 500)
    {
        return _entries
            .Select(static kv => new ComputeRow(kv.Key, kv.Value.Micros, kv.Value.Count))
            .OrderByDescending(static r => r.Micros)
            .ThenBy(static r => r.Key, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    public void EnforceQuota()
    {
        var spent = TotalSpentMicros;
        if (spent > _monthlyBudgetMicros)
        {
            throw new BudgetExhaustedException(spent, _monthlyBudgetMicros);
        }
    }

    public static bool CheckBudget(long spentMicros, long budgetMicros)
        => spentMicros > (budgetMicros > 0 ? budgetMicros : ComputeState.DefaultMonthlyBudgetMicros);
}

/// <summary>
/// Thrown when an actor, app, or external call attempts execution after the monthly compute budget has been exhausted.
/// </summary>
public sealed class BudgetExhaustedException : InvalidOperationException
{
    public BudgetExhaustedException(long spentMicros, long budgetMicros)
        : base($"Compute quota exhausted: {spentMicros} micros spent out of {budgetMicros} micros monthly budget.")
    {
        SpentMicros = spentMicros;
        BudgetMicros = budgetMicros;
    }

    public long SpentMicros { get; }
    public long BudgetMicros { get; }
}
