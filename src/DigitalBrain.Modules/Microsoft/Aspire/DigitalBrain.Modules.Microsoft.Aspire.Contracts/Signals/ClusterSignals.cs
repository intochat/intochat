using DigitalBrain;

namespace DigitalBrain.Microsoft.Aspire;

[GenerateSerializer, Alias("aspire.cluster.silo-state")]
public sealed record SiloState([property: Id(0)] string Id, [property: Id(1)] string Sha, [property: Id(2)] string State, [property: Id(3)] string Health);

[GenerateSerializer, Alias("aspire.cluster.changed")]
public sealed record ClusterChanged([property: Id(0)] string Stage, [property: Id(1)] string Sha, [property: Id(2)] SiloState[] Silos) : Signal;
