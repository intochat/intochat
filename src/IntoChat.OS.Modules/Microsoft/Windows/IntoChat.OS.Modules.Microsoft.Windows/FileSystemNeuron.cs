using IntoChat.OS.Framework.Machine;
using System.Text;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Modules.Microsoft.Windows.Signals;

namespace IntoChat.OS.Modules.Microsoft.Windows;

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
        await PublishAsync(new FileWritten(full, Encoding.UTF8.GetByteCount(content)));
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
