using System.Collections.Concurrent;

namespace IntoChat.OS.Modules.LocalDisk;

// One per host: the local disk neuron builds a storage engine per call and the same folder can be a root of several brains,
// so the lock around a commit belongs to the folder, not to an engine. DI disposes it with the host.
internal sealed class LocalDiskCoordinator : IDisposable
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(Paths);

    internal SemaphoreSlim GateFor(string folder) => _gates.GetOrAdd(folder, static _ => new SemaphoreSlim(1, 1));

    public void Dispose()
    {
        foreach (var gate in _gates.Values) { gate.Dispose(); }
        _gates.Clear();
    }
}
