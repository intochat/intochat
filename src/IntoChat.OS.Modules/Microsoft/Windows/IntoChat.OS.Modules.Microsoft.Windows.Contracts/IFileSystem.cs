using System.ComponentModel;
using Orleans.Concurrency;
using ReadOnly = Orleans.Concurrency.ReadOnlyAttribute;

namespace IntoChat.OS.Microsoft.Windows;

// Read and write files on one Windows PC, keyed {connectionKey}/files, confined to that machine's allowed roots.
// Browsing goes through the storage capability (IStorage) on the connection itself.
[Alias("windows.files"), Orleans.Metadata.DefaultGrainType("windows.files")]
public interface IFileSystem : INeuron
{
    [Description("Read a text file on this Windows machine. path is absolute or relative to the first allowed root.")]
    [ReadOnly, AlwaysInterleave]
    Task<string> Read(string path, CancellationToken cancellationToken = default);

    [Description("Write text to a file on this Windows machine, creating folders as needed; publishes FileWritten.")]
    [AlwaysInterleave]
    Task Write(string path, string content, CancellationToken cancellationToken = default);

    [Description("List the file and folder paths in a folder on this Windows machine.")]
    [ReadOnly, AlwaysInterleave]
    Task<string[]> List(string path, CancellationToken cancellationToken = default);
}
