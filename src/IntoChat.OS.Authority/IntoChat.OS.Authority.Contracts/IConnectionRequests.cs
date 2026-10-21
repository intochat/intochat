using Orleans.Concurrency;

namespace IntoChat.OS.Integrations;

// Request an account of a kind. The secret stays in the platform ring; the script holds an ConnectionRef.
[Alias("integrations.accounts"), Orleans.Metadata.DefaultGrainType("integrations.accounts")]
public interface IConnectionRequests : INeuron
{
    [ReadOnly] Task<ConnectorKind[]> ListKinds();

    [ReadOnly] Task<ConnectionRef[]> ListPending();

    // Keyed by the current brain. Describes existing connections without opening credentials.
    [ReadOnly] Task<ConnectionSummary[]> ListConnections();

    Task<ConnectionRef> Request(string kind);

    // The apps gallery's connections: every declared OAuth connector, whether this host offers it, and
    // whether this brain holds a live connection of it.
    [ReadOnly] Task<ConnectorOffer[]> ListOffers();
}

[GenerateSerializer, Alias("integrations.connector-offer")]
public sealed record ConnectorOffer([property: Id(0)] string Id, [property: Id(1)] string DisplayName, [property: Id(2)] bool Available, [property: Id(3)] bool Connected);

[GenerateSerializer, Alias("integrations.kind")]
public sealed record ConnectorKind([property: Id(0)] string Id, [property: Id(1)] string DisplayName);

[GenerateSerializer, Alias("integrations.connection-summary")]
public sealed record ConnectionSummary(
    [property: Id(0)] string Kind,
    [property: Id(1)] string Id,
    [property: Id(2)] string Neuron,
    [property: Id(3)] string Status,
    [property: Id(4)] string? Account,
    [property: Id(5)] DateTimeOffset? LastProbedAt)
{
    // The capability kinds its connector declares; callers discover storage connections by "storage".
    [Id(6)] public string[] Capabilities { get; init; } = [];
}

[GenerateSerializer, Alias("integrations.account-requested")]
public sealed record ConnectionRequested([property: Id(0)] string Kind, [property: Id(1)] string Id) : Signal;

// Invalidation only: consumers re-read the catalog; no credential or provider detail is carried.
[GenerateSerializer, Alias("integrations.connections-changed")]
public sealed record ConnectionsChanged([property: Id(0)] long Revision) : Signal;
