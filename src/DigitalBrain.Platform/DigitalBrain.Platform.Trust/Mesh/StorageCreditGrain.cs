using System.Globalization;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;

namespace DigitalBrain.Platform.Mesh;

// T23-11, Q21: the storage credits a node owner's account has earned and not yet pulled into its machine-side ledger
// (R60). Credits only; the trust side never charges. Keyed by the node owner's account.
internal interface IStorageCredits : IGrainWithStringKey
{
    // False when the report failed, was already recorded, or the putter is over its daily cap for the node.
    Task<bool> Record(AuditReport report, string putterAccount);

    [AlwaysInterleave]
    Task<StorageCredit[]> Pending();

    Task Acknowledge(string[] entryIds);
}

[GenerateSerializer, Alias("platform.mesh.storage-credit")]
internal sealed record StorageCredit([property: Id(0)] string EntryId, [property: Id(1)] long Micros, [property: Id(2)] string Reason);

internal static class StorageCredits
{
    public static string EntryIdOf(AuditReport report) => $"audit/{report.NodeId}/{report.SegmentCid}/{report.Round}";
}

[GrainType("platform.mesh.storage-credits")]
internal sealed class StorageCreditGrain(
    [PersistentState("storage-credits", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<StorageCreditState> store,
    [FromKeyedServices(MeshOptions.Key)] TimeProvider time, IOptions<StorageCreditOptions> options)
    : Grain, IStorageCredits
{
    private const int RecordedWindow = 10_000;
    private const long GiB = 1L << 30;

    public async Task<bool> Record(AuditReport report, string putterAccount)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(putterAccount);
        if (!report.Passed) { return false; }
        var entryId = StorageCredits.EntryIdOf(report);
        var state = store.State;
        if (state.Recorded.Contains(entryId, StringComparer.Ordinal)) { return false; }

        var day = time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (state.Day != day) { (state.Day, state.Passes) = (day, new Dictionary<string, int>(StringComparer.Ordinal)); }
        var counter = $"{putterAccount}|{report.NodeId}";
        var passes = state.Passes.GetValueOrDefault(counter);
        if (passes >= options.Value.MaxPassesPerPutterPerNodePerDay) { return false; }

        var micros = Math.Max(1, options.Value.MicrosPerGiBPass * report.ShardBytes / GiB);
        state.Passes[counter] = passes + 1;
        state.Pending = [.. state.Pending, new StorageCredit(entryId, micros, $"storage audit {report.NodeId}")];
        string[] recorded = [.. state.Recorded, entryId];
        state.Recorded = recorded.Length > RecordedWindow ? recorded[^RecordedWindow..] : recorded;
        await store.WriteStateAsync();
        return true;
    }

    public Task<StorageCredit[]> Pending() => Task.FromResult(store.State.Pending);

    public async Task Acknowledge(string[] entryIds)
    {
        var acknowledged = new HashSet<string>(entryIds ?? [], StringComparer.Ordinal);
        var remaining = store.State.Pending.Where(credit => !acknowledged.Contains(credit.EntryId)).ToArray();
        if (remaining.Length == store.State.Pending.Length) { return; }
        store.State.Pending = remaining;
        await store.WriteStateAsync();
    }
}

[GenerateSerializer, Alias("platform.mesh.storage-credit-state")]
internal sealed class StorageCreditState
{
    [Id(0)] public StorageCredit[] Pending { get; set; } = [];

    // Entry ids recorded already (a rolling window), so a replayed report records nothing.
    [Id(1)] public string[] Recorded { get; set; } = [];

    // The UTC day the counters count, and per "{putter}|{node}" the passes credited on it.
    [Id(2)] public string Day { get; set; } = "";

    [Id(3)] public Dictionary<string, int> Passes { get; set; } = new(StringComparer.Ordinal);
}
