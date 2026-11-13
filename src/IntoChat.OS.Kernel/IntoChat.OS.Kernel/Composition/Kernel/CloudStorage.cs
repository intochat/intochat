using Azure.Storage.Blobs;
using DigitalBrain.Runtime.Orleans.Azure;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Modules.Microsoft.Orleans;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace IntoChat.OS.Kernel.Composition;

// What the os adds on top of the cloud topology (03-12): the grain state stays in the deployment's container (the module
// option, "os-state" by default, an install's per-format container), and the platform's deployment identity and cookie
// keys keep reaching that container through the keyed grain-state blob clients.
internal static class CloudStorage
{
    private static readonly string[] Stores = [IntoChatOSNames.PlatformGrainStorage];

    public static bool IsCloud(IServiceCollection services)
        => services.Any(descriptor => !descriptor.IsKeyedService && descriptor.ImplementationInstance is CloudProfile);

    public static void ConfigureServices(IServiceCollection services)
    {
        foreach (var name in Stores)
        {
            services.AddOptions<AzureBlobStorageOptions>(name).Configure<IOptions<OrleansOptions>>(
                static (options, module) => options.ContainerName = module.Value.GrainStateContainer);
        }
        services.AddKeyedSingleton(IntoChatOSNames.GrainState, static (services, _) =>
            services.GetRequiredService<CloudStorageAccount>().Blobs());
        services.AddKeyedSingleton<BlobContainerClient>(IntoChatOSNames.GrainState, static (services, _) =>
            services.GetRequiredKeyedService<BlobServiceClient>(IntoChatOSNames.GrainState).GetBlobContainerClient(
                services.GetRequiredService<IOptions<OrleansOptions>>().Value.GrainStateContainer));
    }
}
