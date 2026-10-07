
namespace DigitalBrain.Framework.Platform.Integrations.Accounts;

public enum ConnectionStatus
{
    Connected = 0,
    Expired = 1,
    Failing = 2,
    Disconnected = 3,
}

[GenerateSerializer, Alias("connections.connection")]
public sealed record Connection
{
    [Id(0)] public required string Id { get; init; }
    [Id(1)] public required string IntegrationId { get; init; }
    [Id(2)] public required string WorkspaceId { get; init; }
    [Id(3)] public required SecretRef Credential { get; init; }
    [Id(4)] public required ConnectionStatus Status { get; init; }
    [Id(5)] public DateTimeOffset? LastProbedAt { get; init; }
    [Id(6)] public string AuthMethodId { get; init; } = "";
}
