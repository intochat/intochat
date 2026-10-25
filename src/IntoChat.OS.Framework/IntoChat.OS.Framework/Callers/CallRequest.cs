using IntoChat.OS.Sdk;

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

[GenerateSerializer, Alias("enforcement.call-request")]
public sealed record CallRequest
{
    [Id(0)] public required CallerContext Caller { get; init; }
    [Id(1)] public required string TargetNeuron { get; init; }
    [Id(2)] public required string Operation { get; init; }
    [Id(3)] public string[] SemanticTypeIds { get; init; } = [];
    [Id(4)] public decimal EstimatedCompute { get; init; }
    [Id(5)] public bool HasSideEffects { get; init; }
    [Id(6)] public bool EnforceTarget { get; init; }
    [Id(7)] public string? TargetScope { get; init; }
    [Id(8)] public bool PublicOperation { get; init; }
}
