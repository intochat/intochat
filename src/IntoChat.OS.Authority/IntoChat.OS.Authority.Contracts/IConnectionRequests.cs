namespace IntoChat.OS.Integrations;

// Request an account of a kind. The secret stays in the platform ring; the script holds an ConnectionRef.
// A plain neuron (06-08), the brain's singleton "{brain}/main" (R4-19).
[DigitalBrain.Contracts.NeuronAlias("integrations.accounts"), DigitalBrain.Contracts.DefaultNeuronType("integrations.accounts")]
public interface IConnectionRequests
{
    static string Key(string brainId) => $"{brainId}/main";

    [DigitalBrain.ReadOnlyCall] Task<ConnectorKind[]> ListKinds();

    [DigitalBrain.ReadOnlyCall] Task<ConnectionRef[]> ListPending();

    // Keyed by the current brain. Describes existing connections without opening credentials, in the brain's Settings order
    // (ConnectionSummary.Order): the order the capability broker resolves storage connections in (05-07, R4-02).
    [DigitalBrain.ReadOnlyCall] Task<ConnectionSummary[]> ListConnections();

    // Persists the brain's connection order: the listed keys first, in that order; connections not listed keep their relative
    // order after them, and new connections are appended. Unknown keys are ignored.
    Task Reorder(string[] keys);

    Task<ConnectionRef> Request(string kind);

    // The apps gallery's connections: every declared OAuth connector, whether this host offers it, and
    // whether this brain holds a live connection of it.
    [DigitalBrain.ReadOnlyCall] Task<ConnectorOffer[]> ListOffers();
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
    // Id(6) was the 0.6 string Capabilities (connector-declared kinds); retired by 05-07, never reused (SAFE-01 clean break).

    // The capability ids ("storage@1") the connection's module provides (its ModuleManifest.Provides) and its neuron implements.
    [Id(7)] public string[] Provides { get; init; } = [];

    // The connection's position in the brain's Settings order, from 0; new connections are appended.
    [Id(8)] public int Order { get; init; }
}

[DigitalBrain.Signal("integrations.account-requested")]
public sealed record ConnectionRequested(string Kind, string Id) : DigitalBrain.Signal;

// Invalidation only: consumers re-read the catalog; no credential or provider detail is carried.
[DigitalBrain.Signal("integrations.connections-changed")]
public sealed record ConnectionsChanged(long Revision) : DigitalBrain.Signal;
