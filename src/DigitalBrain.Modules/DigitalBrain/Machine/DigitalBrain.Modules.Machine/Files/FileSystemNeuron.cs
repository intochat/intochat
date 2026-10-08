using DigitalBrain.Framework;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Machine;

[GrainType("machine.files")]
internal sealed class FileSystemNeuron(IOptions<MachineOptions> options) : Neuron, IFileSystem
{
    public async Task<string> Read(string path, CancellationToken cancellationToken = default)
        => await File.ReadAllTextAsync(Resolve(path), cancellationToken);

    public async Task Write(string path, string content, CancellationToken cancellationToken = default)
    {
        var full = Resolve(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
        await File.WriteAllTextAsync(full, content, cancellationToken);
    }

    public Task<string[]> List(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(Directory.GetFileSystemEntries(Resolve(path)));

    private string Resolve(string path)
    {
        RequireEnabled();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = options.Value.Root;
        if (string.IsNullOrWhiteSpace(root)) { return Path.GetFullPath(path); }
        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(normalized, path));
        if (full != normalized && !full.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { throw new UnauthorizedAccessException($"Path '{path}' is outside the machine root."); }
        return full;
    }

    private void RequireEnabled()
    {
        if (!options.Value.Enabled) { throw new InvalidOperationException("The machine module is disabled. Set DigitalBrain:Modules:machine:Options:Enabled=true."); }
    }
}
