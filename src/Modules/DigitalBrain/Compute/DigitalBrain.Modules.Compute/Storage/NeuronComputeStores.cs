using System.Globalization;
using System.Text.Json;
using DigitalBrain.Compute.Metering;
using DigitalBrain.Compute.Usage;
using DigitalBrain.Framework.Metering;

namespace DigitalBrain.Compute.Storage;

internal sealed class NeuronMeterStore(IGrainFactory grains) : IMeterStore
{
    private IComputeRecords Records(string period) => grains.GetGrain<IComputeRecords>("meters/" + period);
    private static string Period(MeterEvent value) => value.OccurredAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    private static ComputeStoredRecord Record(MeterEvent value)
    {
        var id = JsonSerializer.Serialize(new[] { value.IntentId, value.MeterId, value.Step });
        return new(id, JsonSerializer.Serialize(value), value.OccurredAt.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + UsagePaging.Hash(id), 0);
    }

    public async ValueTask<bool> AppendAsync(MeterEvent meterEvent, CancellationToken cancellationToken = default)
        => await AppendBatchAsync([meterEvent], cancellationToken) == 1;

    public async ValueTask<int> AppendBatchAsync(IReadOnlyList<MeterEvent> meterEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meterEvents);
        cancellationToken.ThrowIfCancellationRequested();
        var inserted = 0;
        foreach (var month in meterEvents.GroupBy(Period))
        {
            inserted += await Records(month.Key).Put(month.Select(Record).ToArray(), false);
        }
        return inserted;
    }

    public async ValueTask<IReadOnlyList<MeterEvent>> ReadAsync(string period, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(period);
        cancellationToken.ThrowIfCancellationRequested();
        return (await Records(period).Read()).Select(row => JsonSerializer.Deserialize<MeterEvent>(row.Payload)!).ToArray();
    }

    public ValueTask EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class NeuronUsageStore(IGrainFactory grains) : IUsageStore
{
    private IComputeRecords Records(string account, string brainId) => grains.GetGrain<IComputeRecords>(ComputeRecordsNeuron.UsageKeyPrefix + UsagePaging.Scope(account, brainId));

    private IComputeRecords Ready(string account, string brainId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ct.ThrowIfCancellationRequested();
        return Records(account, brainId);
    }

    public async ValueTask AppendAsync(string account, string brainId, string id, string payload, CancellationToken ct = default, DateTimeOffset? revision = null)
    {
        var records = Ready(account, brainId, ct);
        await records.Put([new(id, payload, UsagePaging.Key(id), (revision ?? DateTimeOffset.UtcNow).UtcTicks)], true);
    }

    public async ValueTask<UsagePage> ReadAsync(string account, string brainId, int limit, string? cursor, CancellationToken ct = default)
    {
        var before = UsagePaging.Decode(account, brainId, limit, cursor);
        var records = Ready(account, brainId, ct);
        var page = await records.Page(before, limit);
        return new(page.Items.Select(row => new UsageRow(row.Id, row.Payload, UsagePaging.OccurredAt(row.SortKey), row.Revision)).ToArray(),
            page.NextKey is null ? null : UsagePaging.Encode(account, brainId, page.NextKey));
    }
}
