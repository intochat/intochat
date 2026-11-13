namespace IntoChat.Sdk;

/// <summary>Permission level granted to a host folder slot binding.</summary>
public enum FolderAccess
{
    Read = 0,
    ReadWrite = 1,
}

/// <summary>Metadata for a file or subdirectory inside a host-mediated folder slot.</summary>
[GenerateSerializer, Alias("sdk.folder-entry")]
public sealed record FolderEntry(
    [property: Id(0)] string Name,
    [property: Id(1)] string RelativePath,
    [property: Id(2)] bool IsDirectory,
    [property: Id(3)] long SizeBytes,
    [property: Id(4)] DateTimeOffset ModifiedUtc);

/// <summary>Signal raised when a watched host folder file is created, modified, renamed, or deleted.</summary>
[DigitalBrain.Signal("windows.file-changed")]
public sealed record FileChanged(
    string Slot,
    string Kind,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset OccurredUtc) : DigitalBrain.Signal;

/// <summary>Mediated host folder capability backed by the kernel and Windows connector.</summary>
public interface IFolder
{
    Task<FolderEntry[]> ListAsync(string relativePath = "", CancellationToken cancellationToken = default);
    Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<byte[]> ReadBytesAsync(string relativePath, CancellationToken cancellationToken = default);
    Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken = default);
    Task WriteBytesAsync(string relativePath, byte[] content, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default);
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
    Task MoveAsync(string sourceRelativePath, string destinationRelativePath, CancellationToken cancellationToken = default);
}
