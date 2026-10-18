using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Compute;
using Orleans.Concurrency;

namespace IntoChat.OS.Authority.Compute;

// Per account. A charge adds a negative entry and is idempotent over (TraceId, CostId); a credit adds a positive
// entry and is idempotent over its entry id. The ledger keeps a bounded window of the last EntryWindow entries;
// older ones fold into an anchor (their chain hash and balance). Every kept entry carries the hash of the chain up
// to it, so a rewritten entry is caught by Verify, and Balance equals the anchor's balance plus the kept entries.
// Every priced call of the account's brains charges here: reentrant, each change is applied at once and shares the
// write that is out (CoalescedWrites), and returns once a write carrying it has landed.
[GrainType("ledger")]
[Reentrant]
internal sealed class LedgerGrain(
    [PersistentState("ledger", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<LedgerState> store)
    : Grain, ILedgerGrain
{
    // A charge is retried within moments, so a short window dedups it; credits are rare and keep a long one.
    private const int ChargeWindow = 1_000;
    private const int CreditWindow = 10_000;
    internal const int EntryWindow = 500;
    private CoalescedWrites? _writes;

    private CoalescedWrites Writes => _writes ??= new(store.WriteStateAsync);

    public Task<long> Balance() => Task.FromResult(store.State.Balance);

    public async Task<bool> Charge(Cost cost)
    {
        ArgumentNullException.ThrowIfNull(cost);
        var entryId = $"{cost.TraceId}/{cost.CostId}";
        if (store.State.RecentKeys.Any(key => string.Equals(key, entryId, StringComparison.Ordinal))) { return false; }
        Append(entryId, -cost.Micros, $"{cost.Kind} {cost.Item}", cost.At);
        store.State.Balance -= cost.Micros;
        store.State.RecentKeys = PushKey(store.State.RecentKeys, entryId, ChargeWindow);
        await Writes.Save();
        return true;
    }

    public async Task<bool> Credit(string entryId, long micros, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        if (store.State.CreditKeys.Any(key => string.Equals(key, entryId, StringComparison.Ordinal))) { return false; }
        Append(entryId, micros, reason ?? "", DateTimeOffset.UtcNow);
        store.State.Balance += micros;
        store.State.CreditKeys = PushKey(store.State.CreditKeys, entryId, CreditWindow);
        await Writes.Save();
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
        var expected = store.State.AnchorHash;
        var sum = store.State.AnchorBalance;
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
        var prevHash = store.State.Entries.Length == 0 ? store.State.AnchorHash : store.State.Entries[^1].Hash;
        LedgerEntry[] next = [.. store.State.Entries,
            new LedgerEntry(entryId, micros, reason, at, prevHash, Digest(ChainText(prevHash, entryId, micros, reason, at)))];
        if (next.Length > EntryWindow)
        {
            var dropped = next[..^EntryWindow];
            store.State.AnchorHash = dropped[^1].Hash;
            store.State.AnchorBalance += dropped.Sum(entry => entry.Micros);
            next = next[^EntryWindow..];
        }
        store.State.Entries = next;
    }

    // SHA-256 hex of "{PrevHash}|{EntryId}|{Micros}|{Reason}|{At:O}".
    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string ChainText(string prevHash, string entryId, long micros, string reason, DateTimeOffset at)
    {
        var stamp = at.ToString("O", CultureInfo.InvariantCulture);
        return $"{prevHash}|{entryId}|{micros}|{reason}|{stamp}";
    }

    private static string[] PushKey(string[] keys, string key, int window)
    {
        string[] next = [.. keys, key];
        return next.Length > window ? next[^window..] : next;
    }
}
