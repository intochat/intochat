namespace DigitalBrain;

[GenerateSerializer, Alias("brain.snapshot")]
public sealed record BrainSnapshot
{
    [Id(0)] public required string BrainId { get; init; }
    [Id(1)] public required string Name { get; init; }
    [Id(2)] public required string OwnerAccountId { get; init; }
    [Id(3)] public DateTimeOffset EstablishedAt { get; init; }
}
