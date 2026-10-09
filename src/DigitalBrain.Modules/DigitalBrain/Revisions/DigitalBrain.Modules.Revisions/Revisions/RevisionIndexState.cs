namespace DigitalBrain.Revisions;

[GenerateSerializer, Alias("revisions.index-state")]
internal sealed record RevisionIndexState
{
    [Id(0)] public string[] Shas { get; init; } = [];
    [Id(1)] public string? Live { get; init; }
    [Id(2)] public string[] Queue { get; init; } = [];
}