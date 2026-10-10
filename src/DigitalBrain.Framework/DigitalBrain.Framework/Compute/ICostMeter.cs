namespace DigitalBrain.Framework.Compute;

public sealed class BudgetExhaustedException(string traceId)
    : Exception($"Trace {traceId} has no budget left.")
{
    public string TraceId { get; } = traceId;
}

public enum FundingKind
{
    Local = 0,
    OwnConnection = 1,
    Integration = 2,
    Circle = 3,
}

// Who funds the resource: the payer's own node, the payer's own connection (billed to them by the
// provider), a deployment integration (billed through the ledger, platform fee included), or a
// circle, whose owner pays through the ledger up to the circle's share of the owner's budget.
public readonly record struct Funding(FundingKind Kind, string? CircleKey = null)
{
    public static Funding Local => new(FundingKind.Local);
    public static Funding OwnConnection => new(FundingKind.OwnConnection);
    public static Funding Integration => new(FundingKind.Integration);

    public static Funding Circle(string circleKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(circleKey);
        return new(FundingKind.Circle, circleKey);
    }
}

// How a circle tells the kernel what share of its owner's budget its members may spend. The kernel
// keeps the share where no neuron can reach it; the circle neuron only states it.
public interface ICircleShares
{
    Task Configure(string circleKey, string ownerAccountId, int percent);
}

public interface ICostMeter
{
    // Prices the quantity, applies funding and fee, and records it against TraceContext.Current.
    // Without a current trace the cost lands on the caller's brain under an untraced trace.
    ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
        string costId, CancellationToken cancellationToken = default);
}
