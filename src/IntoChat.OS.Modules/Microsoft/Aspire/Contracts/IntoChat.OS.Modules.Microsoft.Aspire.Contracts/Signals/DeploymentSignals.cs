using IntoChat.OS;

namespace IntoChat.OS.Modules.Microsoft.Aspire;

[GenerateSerializer, Alias("aspire.deployment.silo-state")]
public sealed record SiloState([property: Id(0)] string Id, [property: Id(1)] string Sha, [property: Id(2)] string State, [property: Id(3)] string Health);

// A plain signal (06-08).
[DigitalBrain.Signal("aspire.deployment.changed")]
public sealed record DeploymentChanged(string Stage, string Sha, SiloState[] Silos) : DigitalBrain.Signal;
