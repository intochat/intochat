using Azure.Storage.Blobs;
using DigitalBrain.Runtime.Orleans.Azure;
using IntoChat.OS.Contracts.Configuration;
using global::Orleans.Configuration;
using global::Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.Orleans;

// What the os adds on top of the cloud topology (03-12): the grain state stays in the deployment's container (the module
// option, "os-state" by default, an install's per-format container), and the platform's deployment identity and cookie
// keys keep reaching that container through the keyed grain-state blob clients.
internal static class CloudStorage
{
    private static readonly string[] Stores = [IntoChatOSNames.PlatformGrainStorage, DigitalBrain.Runtime.Orleans.IntoChatOSNames.PubSubStore];

    public static bool IsCloud(IServiceCollection services)
        => services.Any(descriptor => !descriptor.IsKeyedService && descriptor.ImplementationInstance is CloudProfile);

    public static void Configure(ISiloBuilder silo)
    {
        foreach (var name in Stores)
        {
            silo.Services.AddOptions<AzureBlobStorageOptions>(name).Configure<IOptions<OrleansOptions>>(
                static (options, module) => options.ContainerName = module.Value.GrainStateContainer);
        }
        silo.Services.AddKeyedSingleton(IntoChatOSNames.GrainState, static (services, _) =>
            services.GetRequiredService<CloudStorageAccount>().Blobs());
        silo.Services.AddKeyedSingleton<BlobContainerClient>(IntoChatOSNames.GrainState, static (services, _) =>
            services.GetRequiredKeyedService<BlobServiceClient>(IntoChatOSNames.GrainState).GetBlobContainerClient(
                services.GetRequiredService<IOptions<OrleansOptions>>().Value.GrainStateContainer));
    }
}
