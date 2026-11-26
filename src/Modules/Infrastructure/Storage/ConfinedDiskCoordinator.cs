using System.Collections.Concurrent;

namespace IntoChat.OS.Modules.LocalDisk;

// One per host: confined-disk neurons (local-disk, local-cloud, windows) share root folders across calls and brains,
// so the commit gate belongs to the folder, not to an engine instance. DI disposes it with the host.
internal sealed class ConfinedDiskCoordinator : IDisposable
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
