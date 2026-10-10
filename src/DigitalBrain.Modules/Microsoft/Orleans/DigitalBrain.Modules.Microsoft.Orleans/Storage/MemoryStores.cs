using System.Collections.Concurrent;

namespace DigitalBrain.Microsoft.Orleans;

// The memory-backed grain-state stores of one process family, one per store name. Localhost clustering
// keeps no membership table here. A host that registers one instance before the module configures keeps
// grain state across a rebuild: a silo restart in a test.
public sealed class MemoryStores
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (byte[] State, long ETag)>> _stores = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, (byte[] State, long ETag)> Store(string name) => _stores.GetOrAdd(name, _ => new(StringComparer.Ordinal));
}
