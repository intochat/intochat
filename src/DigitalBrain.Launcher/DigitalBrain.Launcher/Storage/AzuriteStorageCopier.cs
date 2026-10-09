using Azure.Storage;
using Azure.Storage.Blobs;

namespace DigitalBrain.Launcher.Storage;

// Blob-by-blob server-side copy inside the one Azurite account every revision shares; the
// destination container is the child's and is created fresh, never merged.
internal sealed class AzuriteStorageCopier(BlobServiceClient blobs) : IStorageCopier
{
    public const string AccountName = "devstoreaccount1";
    public const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    public static AzuriteStorageCopier At(string blobEndpoint)
        => new(new BlobServiceClient(new Uri(blobEndpoint), new StorageSharedKeyCredential(AccountName, AccountKey)));

    public async Task CopyContainerAsync(string source, string destination, CancellationToken ct)
    {
        var from = blobs.GetBlobContainerClient(source);
        var to = blobs.GetBlobContainerClient(destination);
        await to.DeleteIfExistsAsync(cancellationToken: ct);
        await to.CreateAsync(cancellationToken: ct);
        await foreach (var item in from.GetBlobsAsync(cancellationToken: ct))
        {
            var operation = await to.GetBlobClient(item.Name).StartCopyFromUriAsync(from.GetBlobClient(item.Name).Uri, cancellationToken: ct);
            await operation.WaitForCompletionAsync(ct);
        }
    }
}
