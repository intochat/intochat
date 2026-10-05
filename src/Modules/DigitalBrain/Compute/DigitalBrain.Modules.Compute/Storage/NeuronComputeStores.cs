using DigitalBrain.Framework.Metering;
using System.Globalization;
using System.Text.Json;
using DigitalBrain.Compute.Metering;
using DigitalBrain.Compute.Usage;

namespace DigitalBrain.Compute.Storage;

internal sealed class NeuronMeterStore(IGrainFactory grains) : IMeterStore
{
    private IComputeRecords Records => grains.GetGrain<IComputeRecords>("meters/v1");
    private static ComputeStoredRecord Record(MeterEvent value)
    {
        var id = JsonSerializer.Serialize(new[] { value.IntentId, value.MeterId, value.Step });
        return new(id, JsonSerializer.Serialize(value), value.OccurredAt.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + UsagePaging.Hash(id), 0);
    }

    private IComputeRecords Ready(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Records;
    }

    public async ValueTask<bool> AppendAsync(MeterEvent meterEvent, CancellationToken cancellationToken = default)
        => await AppendBatchAsync([meterEvent], cancellationToken) == 1;

    public async ValueTask<int> AppendBatchAsync(IReadOnlyList<MeterEvent> meterEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meterEvents);
        var records = Ready(cancellationToken);
        // Existing intent batches commit with one root update, including dedup.
        return await records.Put(meterEvents.Select(Record).ToArray(), false);
    }

    public async ValueTask<IReadOnlyList<MeterEvent>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var records = Ready(cancellationToken);
        return (await records.Read()).Select(row => JsonSerializer.Deserialize<MeterEvent>(row.Payload)!).ToArray();
    }

    public ValueTask EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class NeuronUsageStore(IGrainFactory grains) : IUsageStore
{
    private IComputeRecords Records(string account, string workspace) => grains.GetGrain<IComputeRecords>("usage/" + UsagePaging.Scope(account, workspace));

    private IComputeRecords Ready(string account, string workspace, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ct.ThrowIfCancellationRequested();
        return Records(account, workspace);
    }

    public async ValueTask AppendAsync(string account, string workspace, string id, string payload, CancellationToken ct = default, DateTimeOffset? revision = null)
    {
        var records = Ready(account, workspace, ct);
        await records.Put([new(id, payload, UsagePaging.Key(id), (revision ?? DateTimeOffset.UtcNow).UtcTicks)], true);
    }

    public async ValueTask<UsagePage> ReadAsync(string account, string workspace, int limit, string? cursor, CancellationToken ct = default)
    {
        var before = UsagePaging.Decode(account, workspace, limit, cursor);
        var records = Ready(account, workspace, ct);
        var page = await records.Page(before, limit);
        return new(page.Items.Select(row => new UsageRow(row.Id, row.Payload, UsagePaging.OccurredAt(row.SortKey), row.Revision)).ToArray(),
            page.NextKey is null ? null : UsagePaging.Encode(account, workspace, page.NextKey));
    }
}
