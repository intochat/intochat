using DigitalBrain.Framework.Enforcement;

namespace DigitalBrain.Framework.Platform.Integrations.Accounts;

public static class ConnectionNames
{
   
    public const string NeuronType = "connections";
}

// The owner's accounts on external integrations. Credentials are stored in SDK Secrets and records
// contain only references. Platform-only: the caller is read from the ambient stamp, and the
// HTTP endpoints are the user's way in.
[PlatformOnly]
[Alias("connections")]
[Orleans.Metadata.DefaultGrainType(ConnectionNames.NeuronType)]
public interface IConnectionRegistry : INeuron
{
    Task<Connection[]> List(CancellationToken cancellationToken = default);

    Task<Connection> Connect(ConnectConnection request, CancellationToken cancellationToken = default);

    Task<Connection> Probe(string connectionId, CancellationToken cancellationToken = default);

    Task Disconnect(string connectionId, CancellationToken cancellationToken = default);
}

[GenerateSerializer, Alias("connections.connect")]
public sealed record ConnectConnection
{
    [Id(0)] public required string IntegrationId { get; init; }
    [Id(1)] public required string ConnectionId { get; init; }
    [Id(2)] public string? Label { get; init; }

    // Either a pasted connection string / OAuth token (written to the vault here) ...
    [Id(3)] public string? Value { get; init; }

    // ... or a reference into the caller's own vault.
    [Id(4)] public string? SecretReference { get; init; }
    [Id(5)] public string AuthMethodId { get; init; } = "";
}

[GenerateSerializer, Alias("connections.status-changed")]
public sealed record ConnectionStatusChanged(string ConnectionId, string IntegrationId, ConnectionStatus Status) : Signal;

[GenerateSerializer, Alias("connections.disconnected")]
public sealed record ConnectionDisconnected(string ConnectionId) : Signal;
