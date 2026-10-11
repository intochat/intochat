using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;

namespace DigitalBrain.Platform.Compute;

// Per account. A charge appends a negative entry and is idempotent over (TraceId, CostId); a
// credit appends a positive entry and is idempotent over its entry id. Every entry carries the hash of the chain up to it, so a
// rewritten entry is caught by Verify. Balance always equals the sum of the entries.
[GrainType("ledger")]
internal sealed class LedgerGrain(
    [PersistentState("ledger", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<LedgerState> store)
    : Grain, ILedgerGrain
{
    private const int IdempotencyWindow = 10_000;

    public Task<long> Balance() => Task.FromResult(store.State.Balance);

    public async Task<bool> Charge(Cost cost)
    {
        ArgumentNullException.ThrowIfNull(cost);
        var entryId = $"{cost.TraceId}/{cost.CostId}";
        if (store.State.RecentKeys.Any(key => string.Equals(key, entryId, StringComparison.Ordinal))) { return false; }
        Append(entryId, -cost.Micros, $"{cost.Kind} {cost.Item}", cost.At);
        store.State.Balance -= cost.Micros;
        store.State.RecentKeys = PushKey(store.State.RecentKeys, entryId);
        await store.WriteStateAsync();
        return true;
    }

    public async Task<bool> Credit(string entryId, long micros, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        if (store.State.CreditKeys.Any(key => string.Equals(key, entryId, StringComparison.Ordinal))) { return false; }
        Append(entryId, micros, reason ?? "", DateTimeOffset.UtcNow);
        store.State.Balance += micros;
        store.State.CreditKeys = PushKey(store.State.CreditKeys, entryId);
        await store.WriteStateAsync();
        return true;
    }

    public Task<LedgerEntry[]> Entries(int take)
    {
        if (take < 0) { throw new ArgumentException("take must not be negative.", nameof(take)); }
        return Task.FromResult<LedgerEntry[]>([.. store.State.Entries.Reverse().Take(take)]);
    }

    // Recomputes the hash chain and the balance over the stored entries.
    public Task<bool> Verify()
    {
        var expected = "";
        var sum = 0L;
        foreach (var entry in store.State.Entries)
        {
            if (!string.Equals(entry.PrevHash, expected, StringComparison.Ordinal)) { return Task.FromResult(false); }
            if (!string.Equals(entry.Hash, Digest(ChainText(entry.PrevHash, entry.EntryId, entry.Micros, entry.Reason, entry.At)), StringComparison.Ordinal)) { return Task.FromResult(false); }
            sum += entry.Micros;
            expected = entry.Hash;
        }
        return Task.FromResult(sum == store.State.Balance);
    }

    private void Append(string entryId, long micros, string reason, DateTimeOffset at)
    {
        var prevHash = store.State.Entries.Length == 0 ? "" : store.State.Entries[^1].Hash;
        store.State.Entries = [.. store.State.Entries,
            new LedgerEntry(entryId, micros, reason, at, prevHash, Digest(ChainText(prevHash, entryId, micros, reason, at)))];
    }

    // SHA-256 hex of "{PrevHash}|{EntryId}|{Micros}|{Reason}|{At:O}".
    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string ChainText(string prevHash, string entryId, long micros, string reason, DateTimeOffset at)
    {
        var stamp = at.ToString("O", CultureInfo.InvariantCulture);
        return $"{prevHash}|{entryId}|{micros}|{reason}|{stamp}";
    }

    private static string[] PushKey(string[] keys, string key)
    {
        string[] next = [.. keys, key];
        return next.Length > IdempotencyWindow ? next[^IdempotencyWindow..] : next;
    }
}