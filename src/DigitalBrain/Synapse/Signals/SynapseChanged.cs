namespace DigitalBrain;

[GenerateSerializer, Alias("synapse.changed")]
public sealed record SynapseChanged([property: Id(0)] string SynapseId) : Signal;
