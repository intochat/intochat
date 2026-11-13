using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using IntoChat.Sdk;

namespace IntoChat.OS.Modules.Microsoft.Windows;

[GenerateSerializer, Alias("windows.folder-grant")]
public sealed record WindowsFolderGrant(
    [property: Id(0)] string AppKey,
    [property: Id(1)] string Slot,
    [property: Id(2)] string HostPath,
    [property: Id(3)] FolderAccess Access,
    [property: Id(4)] bool Enabled = true);

[Shared]
[NeuronAlias(TypeName), DefaultNeuronType(TypeName)]
public interface IWindowsFolderGateway
{
    const string TypeName = "windows.folder";
    const string DefaultKey = "folders";

    static NeuronId Key(string brainId) => new(new BrainId(brainId), TypeName, DefaultKey);

    Task GrantSlotAsync(WindowsFolderGrant grant, CancellationToken cancellationToken = default);

    Task RevokeAppAsync(string appKey, CancellationToken cancellationToken = default);

    [ReadOnlyCall]
    Task<WindowsFolderGrant[]> ListGrantsAsync(CancellationToken cancellationToken = default);

    [ReadOnlyCall]
    Task<FolderEntry[]> ListAsync(string slot, string relativePath = "", CancellationToken cancellationToken = default);

    [ReadOnlyCall]
    Task<string> ReadTextAsync(string slot, string relativePath, CancellationToken cancellationToken = default);

    [ReadOnlyCall]
    Task<byte[]> ReadBytesAsync(string slot, string relativePath, CancellationToken cancellationToken = default);

    Task WriteTextAsync(string slot, string relativePath, string content, CancellationToken cancellationToken = default);

    Task WriteBytesAsync(string slot, string relativePath, byte[] content, CancellationToken cancellationToken = default);

    [ReadOnlyCall]
    Task<bool> ExistsAsync(string slot, string relativePath, CancellationToken cancellationToken = default);

    Task DeleteAsync(string slot, string relativePath, CancellationToken cancellationToken = default);

    Task MoveAsync(string slot, string sourceRelativePath, string destinationRelativePath, CancellationToken cancellationToken = default);
}
