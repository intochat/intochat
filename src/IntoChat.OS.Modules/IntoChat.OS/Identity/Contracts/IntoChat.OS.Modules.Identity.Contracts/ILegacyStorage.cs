using System.ComponentModel;

namespace IntoChat.OS.Integrations;

// The 0.6 storage contract. storage@1 callers bind IStorage on the same neuron. This surface stays for connection calls.
// Locations are opaque to callers: a Windows path, a Drive file id. The empty location is the storage itself, whose
// children are Roots. Each module enforces its own confinement.
// A plain contract every storage connection implements (06-08): the connection key names its neuron type.
[Obsolete("Use IStorage from DigitalBrain.Capabilities.Storage instead."), IntoChat.OS.Runtime.ConnectionContract]
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

public sealed record StorageEntry(
    string Location,
    string Name,
    bool IsFolder);
