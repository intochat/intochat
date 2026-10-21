using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Integrations;

namespace IntoChat.OS.Microsoft.Windows;

[GenerateSerializer, Alias("windows.connection-state")]
internal sealed class WindowsConnectionState : ConnectionState
{
    [Id(0)] public string[] Roots { get; set; } = [];
}

// The connection to one Windows PC. Its key names the machine; only a silo whose machine id matches serves it,
// and connecting proves the device value is this very machine. As storage, every location is confined
// to the allowed roots, read fresh on every call.
[GrainType("windows")]
internal sealed class WindowsNeuron(
    IMachineIdentity identity,
    [PersistentState("windows", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<WindowsConnectionState> store)
    : ConnectionNeuron<WindowsConnectionState>(store), IWindows, IStorage
{
    public Task<WindowsFacts> Facts() => Task.FromResult(new WindowsFacts(
        identity.Id, Environment.MachineName, Environment.OSVersion.VersionString, Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));

    public Task<string[]> Roots() => Task.FromResult(Snapshot.Roots);

    public async Task AllowRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = MachineRoots.Normalize(path);
        if (!Directory.Exists(full)) { throw new DirectoryNotFoundException($"'{full}' is not a folder on this machine."); }
        if (Snapshot.Roots.Contains(full, StringComparer.OrdinalIgnoreCase)) { return; }
        Snapshot.Roots = [.. Snapshot.Roots, full];
        await SaveQuietly(Snapshot);
    }

    public async Task RevokeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = MachineRoots.Normalize(path);
        Snapshot.Roots = Snapshot.Roots.Where(root => !string.Equals(root, full, StringComparison.OrdinalIgnoreCase)).ToArray();
        await SaveQuietly(Snapshot);
    }

    public Task<StorageEntry[]> Roots(CancellationToken cancellationToken = default) =>
        Task.FromResult(Snapshot.Roots.Select(root => new StorageEntry(root, Name(root), true)).ToArray());

    public Task<StorageEntry[]> List(string location, CancellationToken cancellationToken = default)
    {
        var folder = MachineRoots.Resolve(Snapshot.Roots, location);
        return Task.FromResult(Directory.GetDirectories(folder).Select(path => new StorageEntry(path, Name(path), true))
            .Concat(Directory.GetFiles(folder).Select(path => new StorageEntry(path, Name(path), false))).ToArray());
    }

    public Task<string> Read(string location, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(MachineRoots.Resolve(Snapshot.Roots, location), cancellationToken);

    public Task<StorageEntry[]> Ancestors(string location, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(location)) { return Task.FromResult<StorageEntry[]>([]); }
        var full = MachineRoots.Resolve(Snapshot.Roots, location);
        var root = MachineRoots.Containing(Snapshot.Roots, full);
        var chain = new List<StorageEntry> { new(root, Name(root), true) };
        var path = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            path = Path.Combine(path, part);
            chain.Add(new(path, part, Directory.Exists(path)));
        }
        return Task.FromResult(chain.ToArray());
    }

    private static string Name(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct) =>
        Task.FromResult(OperatingSystem.IsWindows() && credential.Value == identity.Id
            ? ConnectionProbeResult.Connected()
            : ConnectionProbeResult.Failing("This device value is not the machine this silo runs on."));

    internal void RequireThisMachine()
    {
        if (Key.Connection != identity.Id)
        { throw new ServiceUnavailableException($"Windows machine '{Key.Connection}' is not this silo's machine."); }
    }
}

// Neuron seals OnActivateAsync, so the machine check runs as an activation handler: an activation for
// another machine fails before any call (Facts, Connect, Probe) reaches the neuron.
internal sealed class WindowsActivation : IActivationHandler<WindowsNeuron>
{
    public Task Activated(WindowsNeuron neuron, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(neuron);
        neuron.RequireThisMachine();
        return Task.CompletedTask;
    }
}
