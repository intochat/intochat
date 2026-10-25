using DigitalBrain;

namespace IntoChat.Cloud.Circle;

// TOPO-04: the seam through which a machine hands compute to its circle. 0.7.0 ships no compute capability (Phase 5
// D-01), so the only provider answers Unavailable and status reports NotDelegated; 0.8.0 cluster join replaces the
// transport behind this contract without changing apps.
public interface IComputeDelegate
{
    Task<ComputeResult> DelegateCompute(ComputeRequest request);

    ComputeDelegateStatus Status { get; }
}

public sealed record ComputeRequest(string Kind, ReadOnlyMemory<byte> Payload);

public enum ComputeOutcome
{
    Unavailable,
    Completed,
    Failed,
}

public sealed record ComputeResult(ComputeOutcome Outcome, ReadOnlyMemory<byte>? Payload);

public enum ComputeDelegateStatus
{
    NotDelegated,
    Delegated,
}

public sealed record ComputeRequested(string Kind) : Signal;

public sealed record ComputeCompleted(string Kind, ComputeOutcome Outcome) : Signal;

// The 0.7.0 answer: no delegated compute.
public sealed class UnavailableComputeDelegate : IComputeDelegate
{
    public static readonly ComputeResult Unavailable = new(ComputeOutcome.Unavailable, null);

    public ComputeDelegateStatus Status => ComputeDelegateStatus.NotDelegated;

    public Task<ComputeResult> DelegateCompute(ComputeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(Unavailable);
    }
}
