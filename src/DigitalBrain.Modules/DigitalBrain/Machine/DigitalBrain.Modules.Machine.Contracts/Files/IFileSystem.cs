using System.ComponentModel;
using Orleans.Concurrency;
using ReadOnly = Orleans.Concurrency.ReadOnlyAttribute;

namespace DigitalBrain.Machine;

// Reads, writes and lists files on the host machine. When the module is configured with a Root, every
// path is confined to it; the file rail refuses anything that escapes the root.
[Routable]
[Alias("machine.files"), Orleans.Metadata.DefaultGrainType("machine.files")]
public interface IFileSystem : INeuron
{
    [Description("Read a text file from the host machine and return its contents.")]
    [ReadOnly, AlwaysInterleave]
    Task<string> Read(string path, CancellationToken cancellationToken = default);

    [Description("Write text to a file on the host machine, creating directories as needed.")]
    [AlwaysInterleave]
    Task Write(string path, string content, CancellationToken cancellationToken = default);

    [Description("List the file and directory names in a directory on the host machine.")]
    [ReadOnly, AlwaysInterleave]
    Task<string[]> List(string path, CancellationToken cancellationToken = default);
}
