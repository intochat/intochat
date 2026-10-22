namespace IntoChat.OS.Authority.Compute;

[GenerateSerializer, Alias("platform.ledger-state")]
internal sealed class LedgerState
{
    [Id(0)] public long Balance { get; set; }
    [Id(1)] public LedgerEntry[] Entries { get; set; } = [];
    [Id(2)] public string[] RecentKeys { get; set; } = [];

    // R141: credit entry ids in their own window, so charge volume cannot evict them.
    [Id(3)] public string[] CreditKeys { get; set; } = [];

    // What the entries window dropped: the hash the first kept entry chains from, and the sum of the dropped
    // entries, so Verify still proves the chain and the balance over a bounded payload.
    [Id(4)] public string AnchorHash { get; set; } = "";
    [Id(5)] public long AnchorBalance { get; set; }
    [Id(6)] public LedgerSettlement[] UnacknowledgedSettlements { get; set; } = [];
}
