using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Flutter.Compute;

public sealed record ComputeLedgerState(
    ComputeRow[] Rows,
    long MonthlyBudgetMicros = ComputeState.DefaultMonthlyBudgetMicros)
{
    public ComputeLedgerState() : this([], ComputeState.DefaultMonthlyBudgetMicros) { }
}

[Neuron("ui.compute")]
internal sealed class ComputeNeuron : DigitalBrain.Neuron<ComputeLedgerState>, ICompute
{
    public Task<ComputeState> Read()
    {
        EnsureAuthorized();
        return Task.FromResult(CurrentState(State.Rows, State.MonthlyBudgetMicros));
    }

    public async Task<ComputeState> Record(string key, long micros)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(micros);
        EnsureAuthorized();

        var rows = (State.Rows ?? []).ToList();
        var index = rows.FindIndex(row => string.Equals(row.Key, key, StringComparison.Ordinal));
        if (index >= 0)
        {
            var existing = rows[index];
            rows[index] = existing with { Micros = existing.Micros + micros, Count = existing.Count + 1 };
        }
        else
        {
            rows.Add(new ComputeRow(key, micros, 1));
        }

        var ordered = rows.OrderByDescending(row => row.Micros).ThenBy(row => row.Key, StringComparer.Ordinal).Take(500).ToArray();
        var budget = EffectiveBudget(State.MonthlyBudgetMicros);
        var next = new ComputeLedgerState(ordered, budget);
        var state = CurrentState(ordered, budget);
        await Save(next, new ComputeChanged(state.SpentMicros));
        return state;
    }

    public async Task<ComputeState> SetBudget(long monthlyBudgetMicros)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(monthlyBudgetMicros);
        EnsureAuthorized();

        var rows = State.Rows ?? [];
        var next = new ComputeLedgerState(rows, monthlyBudgetMicros);
        var state = CurrentState(rows, monthlyBudgetMicros);
        await Save(next, new ComputeChanged(state.SpentMicros));
        return state;
    }

    private ComputeState CurrentState(ComputeRow[] rows, long monthlyBudgetMicros)
    {
        var active = rows ?? [];
        var spent = active.Sum(static row => row.Micros);
        return new ComputeState(spent, Context.Time.GetUtcNow().AddDays(-30), active, EffectiveBudget(monthlyBudgetMicros));
    }

    private static long EffectiveBudget(long monthlyBudgetMicros)
        => monthlyBudgetMicros > 0 ? monthlyBudgetMicros : ComputeState.DefaultMonthlyBudgetMicros;

    private void EnsureAuthorized()
    {
        if (RuntimeCallerContext.TryGet(out _) && $"{Id.Brain.Value}/{Id.Key}" != BrainScope.CurrentId() + "/compute")
        {
            throw new UnauthorizedAccessException("Read usage only through the current brain's compute neuron.");
        }
    }
}
