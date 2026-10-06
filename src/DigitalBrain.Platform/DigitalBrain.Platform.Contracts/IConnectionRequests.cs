using Orleans.Concurrency;

namespace DigitalBrain.Integrations;

// Request an account of a kind. The secret stays in the platform ring; the script holds an ConnectionRef.
[Alias("integrations.accounts"), Orleans.Metadata.DefaultGrainType("integrations.accounts")]
public interface IConnectionRequests : INeuron
{
    [ReadOnly] Task<ConnectorKind[]> ListKinds();

    [ReadOnly] Task<ConnectionRef[]> ListPending();

    Task<ConnectionRef> Request(string kind);
}

[GenerateSerializer, Alias("integrations.kind")]
public sealed record ConnectorKind([property: Id(0)] string Id, [property: Id(1)] string DisplayName);

[GenerateSerializer, Alias("integrations.account-requested")]
public sealed record ConnectionRequested([property: Id(0)] string Kind, [property: Id(1)] string Id) : Signal;
