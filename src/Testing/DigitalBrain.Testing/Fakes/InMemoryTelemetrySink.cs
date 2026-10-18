using System.Collections;
using System.Diagnostics;
using OpenTelemetry.Logs;

namespace DigitalBrain.Testing;

// What the brain exported in process: every finished span and every log record, in export order.
// The exporters append from the SDK's export threads while facts read, so reads are snapshots taken under a lock.
public sealed class InMemoryTelemetrySink
{
    internal Locked<Activity> Activities { get; } = new();
    internal Locked<LogRecord> Logs { get; } = new();

    public IReadOnlyList<Activity> Spans() => Activities.Snapshot();
    public IReadOnlyList<LogRecord> LogRecords() => Logs.Snapshot();

    // The collection the in-memory exporters append to: every member takes the one lock.
    internal sealed class Locked<T> : ICollection<T>
    {
        private readonly List<T> _items = [];
        private readonly Lock _gate = new();

        public T[] Snapshot() { lock (_gate) { return [.. _items]; } }

        public int Count { get { lock (_gate) { return _items.Count; } } }
        public bool IsReadOnly => false;
        public void Add(T item) { lock (_gate) { _items.Add(item); } }
        public void Clear() { lock (_gate) { _items.Clear(); } }
        public bool Contains(T item) { lock (_gate) { return _items.Contains(item); } }
        public void CopyTo(T[] array, int arrayIndex) { lock (_gate) { _items.CopyTo(array, arrayIndex); } }
        public bool Remove(T item) { lock (_gate) { return _items.Remove(item); } }
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Snapshot()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
