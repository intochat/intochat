// os-only: OS call-filter request descriptor carrying semantic type IDs, compute estimate, and scope enforcement flags
using DigitalBrain.Access;

namespace IntoChat.OS.Framework;

public enum CallDenial
{
    NotAuthenticated = 0,
    UntrustedCaller = 1,
    OutsideWorkspace = 2,
    MissingGrant = 3,
    MissingAllowance = 4,
    LimitReached = 5,
    AppDisabled = 6,
}

public sealed record CallRequest
{
    public required Caller Caller { get; init; }
    public required string TargetNeuron { get; init; }
    public required string Operation { get; init; }
    public string[] SemanticTypeIds { get; init; } = [];
    public decimal EstimatedCompute { get; init; }
    public bool HasSideEffects { get; init; }
    public bool EnforceTarget { get; init; }
    public string? TargetScope { get; init; }
    public bool PublicOperation { get; init; }
}
