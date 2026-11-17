using System.ComponentModel;

namespace IntoChat.OS.Modules.Microsoft.Windows;

// Read files on one Windows PC, keyed {connectionKey}/files, confined to that machine's allowed roots.
// Browsing and every write go through the storage capability (IStorage), whose writes are approved operations.
// A plain neuron (06-08).
[DigitalBrain.Contracts.NeuronAlias("windows.files"), DigitalBrain.Contracts.DefaultNeuronType("windows.files")]
public interface IFileSystem
{
    [Description("Read a text file on this Windows machine. path is absolute or relative to the first allowed root.")]
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<string> Read(string path, CancellationToken cancellationToken = default);

    [Description("List the file and folder paths in a folder on this Windows machine.")]
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<string[]> List(string path, CancellationToken cancellationToken = default);
}
