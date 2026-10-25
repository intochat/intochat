namespace IntoChat.OS.Framework.Caching;

// A count-bounded least-recently-used cache. A full cache evicts its single least-recently-used entry, never the lot.
public sealed class BoundedCache<TKey, TValue> where TKey : notnull
{
    private readonly int capacity;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> entries;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> recency = new();
    private readonly Lock gate = new();

    public BoundedCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
        entries = new(comparer);
    }

    public int Count { get { lock (gate) { return entries.Count; } } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var node))
            {
                recency.Remove(node);
                recency.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                recency.Remove(existing);
                entries.Remove(key);
            }
            else if (entries.Count >= capacity)
            {
                var oldest = recency.Last!;
                recency.RemoveLast();
                entries.Remove(oldest.Value.Key);
            }
            entries[key] = recency.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
        }
    }

    // The factory runs outside the lock, so two callers racing on one key may both build; the later write wins.
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        if (TryGet(key, out var known)) { return known; }
        var created = factory(key);
        Set(key, created);
        return created;
    }
}
