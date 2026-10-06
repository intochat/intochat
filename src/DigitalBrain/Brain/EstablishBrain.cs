namespace DigitalBrain;

[GenerateSerializer, Alias("brain.establish")]
public sealed record EstablishBrain([property: Id(0)] string Name, [property: Id(1)] string OwnerAccountId);
