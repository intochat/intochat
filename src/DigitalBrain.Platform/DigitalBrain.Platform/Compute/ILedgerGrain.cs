using DigitalBrain.Framework.Compute;

namespace DigitalBrain.Platform.Compute;

// The account's billing ledger: the single durable writer of the balance, an append-only
// hash-chained entry list, and a rolling window of idempotency keys. A plain grain, not a
// neuron, so no script can address it.
internal interface ILedgerGrain : IGrainWithStringKey
{
    Task<long> Balance();
    Task<bool> Charge(Cost cost);
    Task Credit(string entryId, long micros, string reason);
    Task<LedgerEntry[]> Entries(int take);
    Task<bool> Verify();
}

[GenerateSerializer, Alias("platform.ledger-entry")]
public sealed record LedgerEntry(
    [property: Id(0)] string EntryId,
    [property: Id(1)] long Micros,
    [property: Id(2)] string Reason,
    [property: Id(3)] DateTimeOffset At,
    [property: Id(4)] string PrevHash,
    [property: Id(5)] string Hash);