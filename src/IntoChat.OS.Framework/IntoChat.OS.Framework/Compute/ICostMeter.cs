namespace IntoChat.OS.Framework.Compute;

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

// How a circle tells the kernel what share of its owner's budget its members may spend. Not a neuron: keyed
// by the circle's owner key, it accepts Configure only from the circle neuron with that very key (the kernel
// names the calling grain), and it learns the owner's account from the kernel, never from the caller.
[Alias("platform.circle-shares")]
public interface ICircleShares : IGrainWithStringKey
{
    Task Configure(int percent, string[] members);
}

// What a cost was spent on, when the caller knows: the synapse that ran, its app, the neuron it served.
public sealed record CostSubject(string? Synapse = null, string? App = null, string? Neuron = null);

public interface ICostMeter
{
    // Prices the quantity, applies funding and fee, and records it against TraceContext.Current.
    // Without a current trace the cost lands on the caller's brain under an untraced trace.
    ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
        string costId, CancellationToken cancellationToken, CostSubject? subject = null);
}
