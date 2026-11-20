using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using IntoChat.OS;
using IntoChat.OS.Authority.Secrets;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Authority.Identity.Directory;

// Azure Blob grain keys do not contain Orleans ServiceId: the store is bound to one deployment's
// service id and master key, so a misconfigured host cannot read or write another's state.
internal sealed class DeploymentStorageIdentity(
    IOptions<ClusterOptions> cluster, IKeyWrapper keys,
    [FromKeyedServices(IntoChatOSNames.GrainState)] BlobContainerClient? storage = null)
{
    private const string MarkerName = ".intochat-os-deployment.json";
    // Key-binding proof text. Never rename: stored deployment markers were wrapped over these exact bytes.
    internal static ReadOnlySpan<byte> KeyBindingContext => "DigitalBrain deployment key binding v1"u8;
    private static readonly byte[] Proof = KeyBindingContext.ToArray();

    public async Task ValidateAsync(CancellationToken ct)
    {
        if (storage is null) { return; } // In-memory unit/development hosts have no durable store.
        var container = storage;
        if (!await container.ExistsAsync(ct)) { return; }
        var marker = await ReadAsync(container, ct);
        if (marker is not null) { Validate(marker, cluster.Value.ServiceId, keys); }
    }

    public async Task BindAsync(CancellationToken ct)
    {
        if (storage is null) { return; }
        await ValidateAsync(ct);
        var container = storage;
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        var current = await ReadAsync(container, ct);
        if (current is not null) { Validate(current, cluster.Value.ServiceId, keys); return; }
        var binding = new Binding(1, cluster.Value.ServiceId, keys.Wrap(Proof));
        try
        {
            await container.GetBlobClient(MarkerName).UploadAsync(BinaryData.FromObjectAsJson(binding),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
        }
        catch (RequestFailedException error) when (error.Status is 409 or 412)
        {
            var winner = await ReadAsync(container, ct) ?? throw new InvalidOperationException("Deployment binding disappeared during initialization.");
            Validate(winner, cluster.Value.ServiceId, keys);
        }
    }

    private static async Task<Binding?> ReadAsync(BlobContainerClient container, CancellationToken ct)
    {
        try
        {
            return (await container.GetBlobClient(MarkerName).DownloadContentAsync(ct)).Value.Content.ToObjectFromJson<Binding>()
                ?? throw new InvalidOperationException("Invalid deployment binding.");
        }
        catch (RequestFailedException error) when (error.Status == 404) { return null; }
    }

    internal static void Validate(Binding binding, string serviceId, IKeyWrapper keys)
    {
        if (binding.Version != 1) { throw new InvalidOperationException("Unsupported deployment binding version."); }
        if (binding.ServiceId != serviceId)
        { throw new OptionsValidationException(nameof(ClusterOptions.ServiceId), typeof(ClusterOptions), ["The configured service ID does not match this persistent deployment."]); }
        try
        {
            if (!keys.Unwrap(binding.KeyProof).SequenceEqual(Proof)) { throw new CryptographicException("Invalid deployment key proof."); }
        }
        catch (CryptographicException)
        { throw new OptionsValidationException(nameof(MasterKeyOptions.MasterKey), typeof(MasterKeyOptions), ["The configured master key does not match this persistent deployment."]); }
    }

    internal sealed record Binding(int Version, string ServiceId, string KeyProof);
}
