using System.ComponentModel;

namespace IntoChat.OS.Integrations;

// The storage capability: a connection that offers it declares IStorage.Capability in its
// connector's Capabilities, and its connection neuron (keyed by the connection key) implements
// IStorage. Locations are opaque to callers: a Windows path, a Drive file id. The empty location
// is the storage itself, whose children are Roots. Each connector enforces its own confinement.
// A plain contract every storage connection implements (06-08): the connection key names its neuron type.
[DigitalBrain.Contracts.NeuronAlias("storage"), IntoChat.OS.Runtime.ConnectionContract]
public interface IStorage
{
    const string Capability = "storage";

    [Description("The top-level folders this storage offers.")]
    Task<StorageEntry[]> Roots(CancellationToken cancellationToken = default);

    [Description("List the files and folders inside a folder location of this storage.")]
    Task<StorageEntry[]> List(string location, CancellationToken cancellationToken = default);

    [Description("Read a text file at a location of this storage.")]
    Task<string> Read(string location, CancellationToken cancellationToken = default);

    [Description("The breadcrumb chain from a root down to and including the location.")]
    Task<StorageEntry[]> Ancestors(string location, CancellationToken cancellationToken = default);
}

[GenerateSerializer, Alias("storage.entry")]
public sealed record StorageEntry(
    [property: Id(0)] string Location,
    [property: Id(1)] string Name,
    [property: Id(2)] bool IsFolder);
