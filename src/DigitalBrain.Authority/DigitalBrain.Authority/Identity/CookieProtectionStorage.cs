using System.Xml.Linq;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Authority.Identity.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Authority.Identity;

public static class CookieProtectionStorage
{
    public const string KeysBlobName = "keys.xml";

    // Keys persist in the grain-state blob store when the built host has one (the orleans integration's,
    // or a client's own reference); a memory host keeps them in process, as its grain state. The choice
    // reads the built provider, so registration order does not matter.
    public static IServiceCollection AddCookieProtection(this IServiceCollection services)
    {
        services.AddOptions<DataProtectionOptions>().Configure<IOptions<IdentityHostOptions>>((protection, host) =>
            protection.ApplicationDiscriminator = host.Value.ProtectionApplicationName);
        services.AddDataProtection();
        services.TryAddSingleton<ProtectionContainer>();
        services.AddOptions<KeyManagementOptions>().Configure<ProtectionContainer>((keys, store) =>
        {
            if (store.Container is { } container)
            { keys.XmlRepository = new BlobKeyRepository(container.GetBlobClient(KeysBlobName)); }
        });
        services.AddHostedService<ContainerStartup>();
        return services;
    }

    internal sealed class ProtectionContainer(IServiceProvider provider, IOptions<IdentityHostOptions> host)
    {
        public BlobContainerClient? Container { get; } = provider.GetKeyedService<BlobServiceClient>(DigitalBrainNames.GrainState)
            ?.GetBlobContainerClient(host.Value.ProtectionContainerName);
    }

    // One XML document of <repository> children, written with an ETag condition so concurrent silos append
    // rather than overwrite (the shape the Azure blob key repository writes).
    internal sealed class BlobKeyRepository(BlobClient blob) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements() => [.. Read().Document.Root!.Elements()];

        public void StoreElement(XElement element, string friendlyName)
        {
            while (true)
            {
                var (document, etag) = Read();
                document.Root!.Add(new XElement(element));
                var conditions = etag is { } match ? new BlobRequestConditions { IfMatch = match } : new BlobRequestConditions { IfNoneMatch = ETag.All };
                try
                {
                    using var stream = new MemoryStream();
                    document.Save(stream);
                    stream.Position = 0;
                    blob.Upload(stream, new BlobUploadOptions { Conditions = conditions });
                    return;
                }
                catch (RequestFailedException failure) when (failure.Status is 409 or 412) { }
            }
        }

        private (XDocument Document, ETag? ETag) Read()
        {
            try
            {
                var content = blob.DownloadContent();
                using var stream = content.Value.Content.ToStream();
                return (XDocument.Load(stream), content.Value.Details.ETag);
            }
            catch (RequestFailedException failure) when (failure.Status == 404)
            { return (new XDocument(new XElement("repository")), null); }
        }
    }

    // A cloud deployment substitutes storage endpoints but does not run Aspire's container provisioning.
    private sealed class ContainerStartup(ProtectionContainer store) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
            => store.Container is { } container ? container.CreateIfNotExistsAsync(cancellationToken: cancellationToken) : Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
