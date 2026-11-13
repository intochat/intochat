using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Flutter.Compute;

[NeuronAlias("compute"), DefaultNeuronType("ui.compute")]
public interface ICompute
{
    [DigitalBrain.Contracts.ReadOnlyCall] Task<ComputeState> Read();
    Task<ComputeState> Record(string key, long micros);
    Task<ComputeState> SetBudget(long monthlyBudgetMicros);
}

public sealed record ComputeState(
    long SpentMicros,
    DateTimeOffset Since,
    ComputeRow[] Rows,
    long MonthlyBudgetMicros = ComputeState.DefaultMonthlyBudgetMicros)
{
    public const long DefaultMonthlyBudgetMicros = 50_000_000L;

    public bool BudgetExceeded => SpentMicros > MonthlyBudgetMicros;
}

public sealed record ComputeRow(
    string Key,
    long Micros,
    long Count);

[DigitalBrain.Signal("ui.compute.changed")]
public sealed record ComputeChanged(long SpentMicros) : DigitalBrain.Signal;
