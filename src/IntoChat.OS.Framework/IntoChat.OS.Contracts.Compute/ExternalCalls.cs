namespace IntoChat.OS.Contracts.Compute;

[GenerateSerializer, Alias("compute.external-call-request")]
public sealed record ExternalCallRequest
{
    [Id(0)] public required string Operation { get; init; }
    [Id(1)] public required decimal Quantity { get; init; }
    [Id(2)] public required string Unit { get; init; }
    [Id(3)] public required Funding Funding { get; init; }
    [Id(4)] public required string InvocationId { get; init; }
    [Id(5)] public required string CostId { get; init; }
    [Id(6)] public CostSubject? Subject { get; init; }
}

// An opaque capability validated by Authority against its durable reservation.
[GenerateSerializer, Alias("compute.external-call-handle")]
public readonly record struct ExternalCallHandle([property: Id(0)] string Value);

public enum ExternalCallOutcomeKind
{
    Succeeded = 0,
    HttpFailed = 1,
    TransportFailed = 2,
    CancelledBeforeDispatch = 3,
    CancelledAfterDispatch = 4,
    Uncertain = 5,
}

[GenerateSerializer, Alias("compute.external-call-outcome")]
public sealed record ExternalCallOutcome(
    [property: Id(0)] ExternalCallOutcomeKind Kind,
    [property: Id(1)] int? HttpStatusCode = null,
    [property: Id(2)] string? ProviderRequestId = null);

public enum ExternalAccountingStatus
{
    Pending = 0,
    Settled = 1,
    CancelledBeforeDispatch = 2,
}

public enum ExternalCallState
{
    Reserved = 0,
    DispatchStarted = 1,
    OutcomeKnown = 2,
    Settled = 3,
    CancelledBeforeDispatch = 4,
    Uncertain = 5,
}

[GenerateSerializer, Alias("compute.external-call-completion")]
public sealed record ExternalCallCompletion(
    [property: Id(0)] ExternalCallHandle Handle,
    [property: Id(1)] ExternalAccountingStatus Status,
    [property: Id(2)] Cost? Cost = null);
