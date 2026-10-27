using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Contracts.Compute;
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
    public Task<bool> ContainsLegacyCharge(string traceId, string costId)
        => Task.FromResult(store.State.RecentKeys.Contains($"{traceId}/{costId}", StringComparer.Ordinal));

    public async Task<bool> Settle(ReservationKey key, Cost cost)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cost.Micros);
        if (key.TraceId != cost.TraceId || key.CostId != cost.CostId) { throw new ArgumentException("Settlement identity does not match the cost."); }
        if (store.State.UnacknowledgedSettlements.FirstOrDefault(value => value.Key == key) is { } previous)
        {
            if (previous.Cost with { At = default } != cost with { At = default }) { throw new InvalidOperationException("Conflicting ledger settlement."); }
            // A preceding write may have failed after the in-memory change. Never acknowledge that change before a successful write.
            await Writes.Save();
            return false;
        }
        // An in-flight RecordAsync call may have charged through the preceding ledger API.
        // Preserve its retained identity when that call retries through the durable coordinator.
        var legacyId = $"{key.TraceId}/{key.CostId}";
        if (cost.Kind != CostKind.ExternalProvider && store.State.RecentKeys.Contains(legacyId, StringComparer.Ordinal))
        {
            store.State.UnacknowledgedSettlements = [.. store.State.UnacknowledgedSettlements, new(key, cost)];
            await Writes.Save();
            return false;
        }
        var balance = checked(store.State.Balance - cost.Micros);
        var entryId = key.BrainId is null ? $"{key.TraceId}/{key.CostId}" : $"{key.BrainId}/{key.TraceId}/{key.CostId}";
        Append(entryId, -cost.Micros, $"{cost.Kind} {cost.Item}", cost.At);
        store.State.Balance = balance;
        store.State.UnacknowledgedSettlements = [.. store.State.UnacknowledgedSettlements, new(key, cost)];
        await Writes.Save();
        return true;
    }

    public async Task AcknowledgeSettlement(ReservationKey key)
    {
        store.State.UnacknowledgedSettlements = [.. store.State.UnacknowledgedSettlements.Where(value => value.Key != key)];
        await Writes.Save();
    }

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
