using IntoChat.OS;

namespace IntoChat.OS.Microsoft.Aspire;

[GenerateSerializer, Alias("aspire.deployment.silo-state")]
public sealed record SiloState([property: Id(0)] string Id, [property: Id(1)] string Sha, [property: Id(2)] string State, [property: Id(3)] string Health);

[GenerateSerializer, Alias("aspire.deployment.changed")]
public sealed record DeploymentChanged([property: Id(0)] string Stage, [property: Id(1)] string Sha, [property: Id(2)] SiloState[] Silos) : Signal;
