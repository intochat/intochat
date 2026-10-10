namespace DigitalBrain.Revisions;

[GenerateSerializer, Alias("revisions.index-state")]
internal sealed record RevisionIndexState
{
    [Id(1)] public string? Live { get; init; }
    [Id(2)] public string[] Queue { get; init; } = [];
}