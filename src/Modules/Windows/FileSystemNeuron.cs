using System.Text;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Errors;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Microsoft.Windows.Signals;

namespace IntoChat.OS.Modules.Microsoft.Windows;

// A plain neuron (06-08) keyed {connectionKey}/files.
[DigitalBrain.Neurons.Neuron("windows.files")]
internal sealed class FileSystemNeuron(MachineIdentity identity, IServiceProvider services) : DigitalBrain.Neurons.Neuron, IFileSystem
{
    public async Task<string> Read(string path, CancellationToken cancellationToken = default) =>
        await File.ReadAllTextAsync(await Resolve(path), cancellationToken);

    public async Task Write(string path, string content, CancellationToken cancellationToken = default)
    {
        var full = await Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, cancellationToken);
        await Publish(new FileWritten(full, Encoding.UTF8.GetByteCount(content)));
    }

    public async Task<string[]> List(string path, CancellationToken cancellationToken = default) =>
        Directory.GetFileSystemEntries(await Resolve(path));

    // Roots are read on every call, so a revoked root is refused by the very next call.
    private async Task<string> Resolve(string path)
    {
        var connection = WindowsKeys.Connection($"{Id.Brain.Value}/{Id.Key}");
        if (ConnectionKeys.Parse(connection).ConnectionId != identity.Id)
        { throw new ServiceUnavailableException("This Windows machine is not this silo's machine."); }
        return PathConfinement.Resolve(await SiloNeurons.Get<IWindows>(services, connection).Roots(), path);
    }
}
