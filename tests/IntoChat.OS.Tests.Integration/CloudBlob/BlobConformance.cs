using System.Net.Sockets;
using Azure.Storage.Blobs;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Testing.Conformance.Storage;

namespace IntoChat.Cloud.Blob.Tests.Integration;

public sealed class BlobConformance : StorageConformance<BlobStorageNeuron>
{
    protected override async Task<StorageSubject> Connect(StorageSeed seed, CancellationToken ct)
    {
        if (!await PortOpen(ct).ConfigureAwait(false)) { Assert.Skip("Azurite is not listening on 127.0.0.1:10000."); }
        var container = BlobStorage.Open(BlobStorage.AzuriteConnection).GetBlobContainerClient(BlobContainerName.For(Guid.NewGuid().ToString("N")));
        await container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        await Parallel.ForEachAsync(seed.Entries, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (entry, token) =>
        {
            var path = entry.Kind == ItemKind.Folder ? entry.Path + "/.keep" : entry.Path;
            await container.GetBlobClient(path).UploadAsync(BinaryData.FromBytes(entry.Bytes), token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        var operations = new InMemoryOperations();
        return new StorageSubject(new BlobStorage(container, operations), operations,
            [BlobItemId.Of("../escape"), BlobItemId.Of("a/../b")]);
    }

    protected override ItemId MissingId() => BlobItemId.Of($"missing-{Guid.NewGuid():N}.txt");

    private static async Task<bool> PortOpen(CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", 10000, ct).ConfigureAwait(false);
            return true;
        }
        catch (SocketException) { return false; }
    }
}
