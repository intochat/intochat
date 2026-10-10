namespace DigitalBrain.Platform.Compute;

[GenerateSerializer, Alias("platform.ledger-state")]
internal sealed class LedgerState
{
    [Id(0)] public long Balance { get; set; }
    [Id(1)] public LedgerEntry[] Entries { get; set; } = [];
    [Id(2)] public string[] RecentKeys { get; set; } = [];
}