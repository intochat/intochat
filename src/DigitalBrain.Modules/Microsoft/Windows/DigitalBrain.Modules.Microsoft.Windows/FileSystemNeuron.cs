using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;

namespace DigitalBrain.Microsoft.Windows;

[GrainType("windows.files")]
internal sealed class FileSystemNeuron(IMachineIdentity identity) : Neuron, IFileSystem
{
    public async Task<string> Read(string path, CancellationToken cancellationToken = default) =>
        await File.ReadAllTextAsync(await Resolve(path), cancellationToken);

    public async Task Write(string path, string content, CancellationToken cancellationToken = default)
    {
        var full = await Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, cancellationToken);
    }

    public async Task<string[]> List(string path, CancellationToken cancellationToken = default) =>
        Directory.GetFileSystemEntries(await Resolve(path));

    // Roots are read on every call, so a revoked root is refused by the very next call.
    private async Task<string> Resolve(string path)
    {
        var connection = WindowsKeys.Connection(this.GetPrimaryKeyString());
        if (ConnectionKeys.Parse(connection).ConnectionId != identity.Id)
        { throw new ServiceUnavailableException("This Windows machine is not this silo's machine."); }
        return MachineRoots.Resolve(await GrainFactory.GetGrain<IWindows>(connection).Roots(), path);
    }
}
