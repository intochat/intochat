using Azure.Storage;
using Azure.Storage.Blobs;

namespace DigitalBrain.Microsoft.Aspire.Candidates;

// Blob-by-blob copy between two Azurite accounts. A cross-account StartCopyFromUri needs a SAS the
// emulator's well-known key does not hand out, so every blob is downloaded and uploaded.
internal static class AzuriteCopy
{
    public const string AccountName = "devstoreaccount1";
    public const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    public static BlobServiceClient At(string blobEndpoint)
        => new(new Uri(blobEndpoint), new StorageSharedKeyCredential(AccountName, AccountKey));

    public static async Task CopyAsync(BlobServiceClient from, string sourceContainer, BlobServiceClient to, string destinationContainer, Func<string, bool> skip, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(skip);
        var source = from.GetBlobContainerClient(sourceContainer);
        var destination = to.GetBlobContainerClient(destinationContainer);
        await destination.DeleteIfExistsAsync(cancellationToken: ct);
        await destination.CreateAsync(cancellationToken: ct);
        if (!await source.ExistsAsync(ct)) { return; }
        await foreach (var item in source.GetBlobsAsync(cancellationToken: ct))
        {
            if (skip(item.Name)) { continue; }
            var content = await source.GetBlobClient(item.Name).DownloadContentAsync(ct);
            await destination.GetBlobClient(item.Name).UploadAsync(content.Value.Content, overwrite: true, ct);
        }
    }
}
