namespace DigitalBrain.Platform.Compute;

// Per trace id: the running total a trace has spent, so a budgeted trace cannot overspend.
// A plain grain, not a neuron, so no script can address it.
internal interface ITraceBudgetGrain : IGrainWithStringKey
{
    Task<bool> TrySpend(long micros, long budget);
}