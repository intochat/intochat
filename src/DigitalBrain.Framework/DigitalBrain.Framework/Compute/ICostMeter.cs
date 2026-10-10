
namespace DigitalBrain.Framework.Compute;

public sealed class BudgetExhaustedException(string traceId)
    : Exception($"Trace {traceId} has no budget left.")
{
    public string TraceId { get; } = traceId;
}

// Who funds the resource: the payer's own node, the payer's own connection (billed to them by the
// provider), or a deployment integration (billed through the ledger, platform fee included).
public enum Funding
{
    Local = 0,
    OwnConnection = 1,
    Integration = 2,
}

public interface ICostMeter
{
    // Prices the quantity, applies funding and fee, and records it against TraceContext.Current.
    // Without a current trace the cost lands on the caller's brain under an untraced trace.
    ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
        string costId, CancellationToken cancellationToken = default);
}
