using System.Collections.Concurrent;
using System.Globalization;
using DigitalBrain.Framework.Metering;

namespace DigitalBrain.Compute.Metering;

// Append-only meter store with the idempotency key (IntentId, MeterId, Step) as its
// uniqueness constraint. A repeated key is accepted and discarded, never overwritten.
internal sealed class InMemoryMeterStore : IMeterStore
{
    private readonly ConcurrentDictionary<(string IntentId, string MeterId, string Step), MeterEvent> events = new();

    public ValueTask<bool> AppendAsync(MeterEvent meterEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meterEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(events.TryAdd(Key(meterEvent), meterEvent));
    }

    public ValueTask<int> AppendBatchAsync(IReadOnlyList<MeterEvent> meterEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meterEvents);
        cancellationToken.ThrowIfCancellationRequested();
        var inserted = 0;
        foreach (var meterEvent in meterEvents)
        {
            if (events.TryAdd(Key(meterEvent), meterEvent)) { inserted++; }
        }

        return ValueTask.FromResult(inserted);
    }

    public ValueTask<IReadOnlyList<MeterEvent>> ReadAsync(string period, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MeterEvent> snapshot = [.. events.Values.Where(meter => meter.OccurredAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture) == period).OrderBy(meter => meter.OccurredAt)];
        return ValueTask.FromResult(snapshot);
    }

    public ValueTask EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    internal static (string IntentId, string MeterId, string Step) Key(MeterEvent meterEvent)
        => (meterEvent.IntentId, meterEvent.MeterId, meterEvent.Step);
}
