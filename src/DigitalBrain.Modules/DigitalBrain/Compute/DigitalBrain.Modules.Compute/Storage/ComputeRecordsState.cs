namespace DigitalBrain.Compute.Storage;

[GenerateSerializer]
internal sealed class ComputeRecordsState
{
    [Id(0)] public string? Root { get; set; }
    [Id(2)] public ComputeStoredRecord[] Records { get; set; } = [];
}

[GenerateSerializer]
internal sealed record ComputeStoredRecord(
    [property: Id(0)] string Id,
    [property: Id(1)] string Payload,
    [property: Id(2)] string SortKey,
    [property: Id(3)] long Revision);
