using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using DigitalBrain.Framework;
using global::Orleans.Configuration;
using global::Orleans.Hosting;
using global::Orleans.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Microsoft.Orleans;

public static class OrleansStorageHosting
{
    public const string ClusteringField = "Clustering";
    public const string GrainStateField = "GrainState";
    public const string RemindersField = "Reminders";
    public const string GrainStateContainerField = "GrainStateContainer";

    private static readonly string[] GrainStores = [DigitalBrainNames.ClusterGrainStorage, DigitalBrainNames.PlatformGrainStorage];

    // Providers must exist before the silo builds, so the choice is read from configuration here, not
    // from the integration grain at runtime; the Platform seeds the same keys into the integration.
    public static ISiloBuilder AddOrleansStorage(this ISiloBuilder silo, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(configuration);
        var clustering = StorageEndpoint.Read(configuration, ClusteringField);
        var grainState = StorageEndpoint.Read(configuration, GrainStateField);
        var reminders = StorageEndpoint.Read(configuration, RemindersField);
        var fields = new[] { (Field: ClusteringField, Value: clustering), (Field: GrainStateField, Value: grainState), (Field: RemindersField, Value: reminders) };
        var missing = fields.Where(pair => pair.Value is null).Select(pair => pair.Field).ToArray();
        if (missing.Length == fields.Length) { return AddMemory(silo, configuration); }
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"The orleans integration is partial; missing: {string.Join(", ", missing)}. Set all three or none.");
        }
        return AddAzure(silo, clustering!, grainState!, reminders!, configuration[OrleansModule.Integration.Key(GrainStateContainerField)]);
    }

    // Orleans' own endpoint keys pick the ports, so two localhost silos in one machine (parallel test
    // brains) each bind their own pair; absent, the Orleans defaults.
    public const string SiloPortKey = "Orleans:Endpoints:SiloPort";
    public const string GatewayPortKey = "Orleans:Endpoints:GatewayPort";

    private static ISiloBuilder AddMemory(ISiloBuilder silo, IConfiguration configuration)
    {
        silo.Services.TryAddSingleton<MemoryStores>();
        silo.UseLocalhostClustering(
            configuration.GetValue(SiloPortKey, EndpointOptions.DEFAULT_SILO_PORT),
            configuration.GetValue(GatewayPortKey, EndpointOptions.DEFAULT_GATEWAY_PORT));
        silo.UseInMemoryReminderService();
        foreach (var name in GrainStores)
        {
            silo.Services.AddKeyedSingleton<IGrainStorage>(name, static (provider, key) => new MemoryStoreGrainStorage(
                provider.GetRequiredService<MemoryStores>().Store((string)key!),
                provider.GetRequiredService<global::Orleans.Serialization.Serializer>()));
        }
        return silo;
    }

    private static ISiloBuilder AddAzure(ISiloBuilder silo, StorageEndpoint clustering, StorageEndpoint grainState, StorageEndpoint reminders, string? container)
    {
        // Service URIs authenticate with the deployment identity, registered once like the C# module's.
        if (new[] { clustering, grainState, reminders }.Any(endpoint => endpoint.ServiceUri is not null))
        { silo.Services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential()); }
        silo.UseAzureStorageClustering(options => options.Configure<IServiceProvider>((o, services) => o.TableServiceClient = Tables(clustering, services)));
        silo.UseAzureTableReminderService(options => options.Configure<IServiceProvider>((o, services) => o.TableServiceClient = Tables(reminders, services)));
        // The platform's deployment identity and cookie keys live beside the grain state.
        silo.Services.AddKeyedSingleton(DigitalBrainNames.GrainState, (services, _) => grainState.ServiceUri is { } uri
            ? new BlobServiceClient(uri, services.GetRequiredService<TokenCredential>())
            : new BlobServiceClient(grainState.ConnectionString));
        silo.Services.AddKeyedSingleton<BlobContainerClient>(DigitalBrainNames.GrainState, (services, _) =>
            services.GetRequiredKeyedService<BlobServiceClient>(DigitalBrainNames.GrainState).GetBlobContainerClient(
                services.GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>().Get(DigitalBrainNames.PlatformGrainStorage).ContainerName));
        foreach (var name in GrainStores)
        {
            silo.AddAzureBlobGrainStorage(name, options => options.Configure<IServiceProvider>((o, services) =>
                o.BlobServiceClient = services.GetRequiredKeyedService<BlobServiceClient>(DigitalBrainNames.GrainState)));
            silo.Services.AddKeyedSingleton<IGrainStorageSerializer>(name, static (services, _) =>
                new OrleansGrainStorageSerializer(services.GetRequiredService<global::Orleans.Serialization.Serializer>()));
            // The integration setting wins; the module option is the deployment default.
            silo.Services.AddOptions<AzureBlobStorageOptions>(name).Configure<IOptions<OrleansOptions>>(
                (options, module) => options.ContainerName = string.IsNullOrWhiteSpace(container) ? module.Value.GrainStateContainer : container);
        }
        return silo;
    }

    private static TableServiceClient Tables(StorageEndpoint endpoint, IServiceProvider services) => endpoint.ServiceUri is { } uri
        ? new TableServiceClient(uri, services.GetRequiredService<TokenCredential>())
        : new TableServiceClient(endpoint.ConnectionString);
}
