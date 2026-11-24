using System.Collections.Concurrent;
using DigitalBrain;
using IntoChat.Aspire.Domain;

namespace IntoChat.Aspire.Bridge;

// Per-process in-memory projection of the running AppHost's resources.
// No Orleans types, no I/O, no heartbeat polling: Connected tracks the live UDS stream directly.
internal sealed class AspireHostStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, BridgeSnapshot> _instances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<BrainId, byte> _brains = new();
    private volatile bool _connected;

    public bool Connected => _connected;

    public BrainId[] Brains => [.. _brains.Keys];

    public void SetConnected(bool connected) => _connected = connected;

    public void Register(BrainId brain) => _brains.TryAdd(brain, 0);

    public void Unregister(BrainId brain) => _brains.TryRemove(brain, out _);

    public RuntimeTopology Snapshot()
    {
        lock (_gate)
        {
            var grouped = _instances.Values
                .GroupBy(static s => s.Name, StringComparer.Ordinal)
                .OrderBy(static g => g.Key, StringComparer.Ordinal)
                .Select(static g =>
                {
                    var first = g.First();
                    var views = g
                        .OrderBy(static s => s.InstanceId, StringComparer.Ordinal)
                        .Select(static s => new AspireInstanceView(
                            s.InstanceId,
                            s.State,
                            s.Health,
                            s.Urls,
                            s.StartedAt,
                            s.StoppedAt,
                            s.Commands))
                        .ToArray();
                    return new AspireResource(g.Key, first.Type, first.Parent, views);
                })
                .ToArray();

            return new RuntimeTopology(_connected, grouped);
        }
    }

    public AspireResources Read() => Snapshot().ToContract();

    // Applies a snapshot and returns a change signal only when State or Health actually changed (or instance was added/removed).
    public IReadOnlyList<AspireResourceChanged> Apply(BridgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (snapshot.Removed)
            {
                if (_instances.Remove(snapshot.InstanceId, out var prev))
                {
                    return [new AspireResourceChanged(prev.Name, prev.InstanceId, "Removed", null)];
                }

                return [];
            }

            var changed = !_instances.TryGetValue(snapshot.InstanceId, out var existing)
                || !string.Equals(existing.State, snapshot.State, StringComparison.Ordinal)
                || !string.Equals(existing.Health, snapshot.Health, StringComparison.Ordinal);

            _instances[snapshot.InstanceId] = snapshot;
            return changed
                ? [new AspireResourceChanged(snapshot.Name, snapshot.InstanceId, snapshot.State, snapshot.Health)]
                : [];
        }
    }
}
