using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Compute;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Compute;

[GrainType("compute-trace-journal")]
internal sealed class TraceJournalGrain(
    [PersistentState("compute-trace-journal", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<TraceJournalState> store,
    IOptions<ComputeOptions> options, TimeProvider clock) : Grain, ITraceJournalGrain
{
    public async Task Append(Cost cost, long revision)
    {
        if (revision <= 0) { throw new ArgumentOutOfRangeException(nameof(revision)); }
        if (store.State.Expired) { store.State = new(); }
        if (store.State.Revisions.FirstOrDefault(value => value.Sequence == revision) is { } prior)
        {
            if (prior.Cost != cost) { throw new InvalidOperationException("Conflicting trace revision."); }
            await store.WriteStateAsync();
            return;
        }
        if (revision <= store.State.Watermark) { return; }
        if (store.State.CreatedAt == default) { store.State.CreatedAt = clock.GetUtcNow(); }
        store.State.Watermark = revision;
        if (store.State.Revisions.Length < options.Value.MaxTraceRevisions)
        { store.State.Revisions = [.. store.State.Revisions, new(revision, cost)]; }
        else { store.State.Incomplete = true; }
        await store.WriteStateAsync();
    }

    public async Task<ComputeTracePage> Read(int take, string? continuationToken)
    {
        if (take is < 1 or > ComputeEndpoints.MaximumTake) { throw new ArgumentOutOfRangeException(nameof(take)); }
        var (watermark, offset) = continuationToken is null ? (store.State.Watermark, 0) : Decode(continuationToken);
        var hasPending = store.State.Revisions.GroupBy(value => value.Cost.CostId, StringComparer.Ordinal)
            .Any(group => IsPending(group.MaxBy(value => value.Sequence)!.Cost));
        if (store.State.Expired || !hasPending && store.State.CreatedAt != default && clock.GetUtcNow() - store.State.CreatedAt > options.Value.SettledRetention)
        {
            await Expire();
            return Page([], 0, null, TraceDetailStatus.Expired);
        }
        if (store.State.CreatedAt == default) { return Page([], 0, null, TraceDetailStatus.DetailUnavailable); }
        var rows = store.State.Revisions.Where(value => value.Sequence <= watermark)
            .GroupBy(value => value.Cost.CostId, StringComparer.Ordinal)
            .OrderBy(group => group.Min(value => value.Sequence))
            .Select(group => group.MaxBy(value => value.Sequence)!.Cost).ToArray();
        if (offset > rows.Length) { throw new ArgumentException("Invalid trace continuation.", nameof(continuationToken)); }
        var page = rows.Skip(offset).Take(take).ToArray();
        var token = offset + page.Length < rows.Length ? Encode(watermark, offset + page.Length) : null;
        return Page(rows, watermark, token, store.State.Incomplete ? TraceDetailStatus.Incomplete : TraceDetailStatus.Complete) with { Rows = page };
    }

    public async Task Expire()
    {
        await store.ClearStateAsync();
        // Retain the answer only for this activation; no permanent tombstone or signing secret remains in storage.
        store.State = new TraceJournalState { Expired = true };
    }

    private static ComputeTracePage Page(Cost[] rows, long watermark, string? token, TraceDetailStatus status) => new()
    {
        Rows = rows,
        ChargedMicros = rows.Sum(value => value.Micros),
        KnownListPriceMicros = rows.Where(value => value.ChargePolicy != ExternalChargePolicy.Informational).Sum(value => value.ListPriceMicros ?? 0),
        UnpricedCount = rows.LongCount(value => value.PricingStatus == PricingStatus.Unpriced),
        PendingCount = rows.LongCount(IsPending),
        UnknownCount = rows.LongCount(value => value.PricingStatus is null or PricingStatus.Unknown),
        SnapshotWatermark = watermark,
        ContinuationToken = token,
        Status = status,
    };

    internal static bool IsPending(Cost cost) => cost.AttemptState is ExternalCallState.Reserved or ExternalCallState.DispatchStarted or ExternalCallState.OutcomeKnown or ExternalCallState.Uncertain;

    private string Encode(long watermark, int offset)
    {
        var payload = string.Create(CultureInfo.InvariantCulture, $"{watermark}:{offset}");
        var signature = HMACSHA256.HashData(Convert.FromBase64String(store.State.CursorSecret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload + ":" + Convert.ToHexString(signature)));
    }

    private (long Watermark, int Offset) Decode(string token)
    {
        try
        {
            if (token.Length > 256) { throw new FormatException(); }
            var values = Encoding.UTF8.GetString(Convert.FromBase64String(token)).Split(':');
            if (values.Length != 3 || !long.TryParse(values[0], CultureInfo.InvariantCulture, out var watermark)
                || !int.TryParse(values[1], CultureInfo.InvariantCulture, out var offset) || watermark < 0 || watermark > store.State.Watermark || offset < 0)
            { throw new FormatException(); }
            var expected = HMACSHA256.HashData(Convert.FromBase64String(store.State.CursorSecret), Encoding.UTF8.GetBytes(values[0] + ":" + values[1]));
            if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(values[2]))) { throw new FormatException(); }
            return (watermark, offset);
        }
        catch (FormatException exception) { throw new ArgumentException("Invalid trace continuation.", nameof(token), exception); }
    }
}
