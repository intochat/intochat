using Azure.Storage.Blobs;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Identity;

public static class CookieProtectionStorage
{
    // Keys persist in the grain-state blob store when the host has one (the orleans integration's, or a
    // client's own reference); a memory host keeps them in process, as its grain state.
    public static IServiceCollection AddCookieProtection(this IServiceCollection services)
    {
        services.AddOptions<DataProtectionOptions>().Configure<IOptions<IdentityHostOptions>>((protection, host) =>
            protection.ApplicationDiscriminator = host.Value.ProtectionApplicationName);
        if (!services.Any(d => d.ServiceType == typeof(BlobServiceClient) && Equals(d.ServiceKey, DigitalBrainNames.GrainState)))
        {
            services.AddDataProtection();
            return services;
        }
        services.AddSingleton(provider => provider
            .GetRequiredKeyedService<BlobServiceClient>(DigitalBrainNames.GrainState)
            .GetBlobContainerClient(provider.GetRequiredService<IOptions<IdentityHostOptions>>().Value.ProtectionContainerName));
        services.AddHostedService<ContainerStartup>();
        services.AddDataProtection()
            .PersistKeysToAzureBlobStorage(provider => provider.GetRequiredService<BlobContainerClient>().GetBlobClient("keys.xml"));
        return services;
    }

    // DeploymentKit substitutes storage endpoints but does not execute Aspire's container provisioning.
    private sealed class ContainerStartup(BlobContainerClient container) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
            => container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
