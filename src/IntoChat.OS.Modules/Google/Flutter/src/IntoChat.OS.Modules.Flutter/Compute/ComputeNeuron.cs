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
    private ComputeQuotaLedger? _ledger;

    private ComputeQuotaLedger Ledger => _ledger ??= new ComputeQuotaLedger(State.Rows ?? [], State.MonthlyBudgetMicros);

    public Task<ComputeState> Read()
    {
        EnsureAuthorized();
        var rows = Ledger.ToRows();
        var budget = EffectiveBudget(State.MonthlyBudgetMicros);
        return Task.FromResult(CurrentState(rows, budget));
    }

    public async Task<ComputeState> Record(string key, long micros)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(micros);
        EnsureAuthorized();

        Ledger.Record(key, micros);
        var ordered = Ledger.ToRows();
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

        Ledger.MonthlyBudgetMicros = monthlyBudgetMicros;
        var rows = Ledger.ToRows();
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
