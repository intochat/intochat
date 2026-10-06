namespace DigitalBrain.Compute;

public enum AllowanceCallerKind { User = 0, Assistant = 1, App = 2, Scheduler = 3, Platform = 4 }

public enum AllowanceDenial
{
    NotAuthenticated = 0,
    UntrustedCaller = 1,
    OutsideWorkspace = 2,
    MissingGrant = 3,
    MissingAllowance = 4,
    LimitReached = 5,
    AppDisabled = 6,
}

[GenerateSerializer, Alias("compute.allowance-request")]
public sealed record AllowanceRequest(
    [property: Id(0)] string AccountId,
    [property: Id(1)] string BrainId,
    [property: Id(2)] string? AppId,
    [property: Id(3)] string? IntentId,
    [property: Id(4)] AllowanceCallerKind Kind,
    [property: Id(5)] string TargetNeuron,
    [property: Id(6)] string Operation,
    [property: Id(7)] string[] SemanticTypeIds,
    [property: Id(8)] decimal EstimatedCompute,
    [property: Id(9)] bool HasSideEffects,
    [property: Id(10)] string? ConversationId = null);
