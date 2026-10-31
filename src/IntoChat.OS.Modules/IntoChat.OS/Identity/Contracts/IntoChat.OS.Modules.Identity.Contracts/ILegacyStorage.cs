using System.ComponentModel;

namespace IntoChat.OS.Integrations;

// The 0.6 storage contract preserved for binary compatibility with 0.10.0 published module assemblies.
[EditorBrowsable(EditorBrowsableState.Never), IntoChat.OS.Runtime.ConnectionContract]
public interface ILegacyStorage
{
    [Description("The top-level folders this storage offers.")]
    Task<StorageEntry[]> Roots(CancellationToken cancellationToken = default);

    [Description("List the files and folders inside a folder location of this storage.")]
    Task<StorageEntry[]> List(string location, CancellationToken cancellationToken = default);

    [Description("Read a text file at a location of this storage.")]
    Task<string> Read(string location, CancellationToken cancellationToken = default);

    [Description("The breadcrumb chain from a root down to and including the location.")]
    Task<StorageEntry[]> Ancestors(string location, CancellationToken cancellationToken = default);
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record StorageEntry(
    string Location,
    string Name,
    bool IsFolder);
