using System.Collections;

namespace IntoChat.OS.Sdk.Generators.Models;

// Sequence-equal wrapper so incremental models compare by value and the pipeline caches.
internal readonly struct EquatableArray<T>(T[] items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[] _items = items ?? [];

    public int Length => _items.Length;
    public T this[int index] => _items[index];

    public bool Equals(EquatableArray<T> other)
    {
        var left = _items ?? [];
        var right = other._items ?? [];
        if (left.Length != right.Length) { return false; }
        for (var i = 0; i < left.Length; i++)
        {
            if (!left[i].Equals(right[i])) { return false; }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in _items ?? [])
        {
            hash = unchecked(hash * 31 + item.GetHashCode());
        }
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
