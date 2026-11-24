using IntoChat.OS;

namespace IntoChat.Aspire;

public sealed record SiloState(string Id, string Sha, string State, string Health);

// A plain signal (06-08).
[DigitalBrain.Signal("aspire.deployment.changed")]
public sealed record DeploymentChanged(string Stage, string Sha, SiloState[] Silos) : DigitalBrain.Signal;
