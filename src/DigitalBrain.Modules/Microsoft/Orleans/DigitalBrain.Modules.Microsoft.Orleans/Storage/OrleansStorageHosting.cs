using Azure.Data.Tables;
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
        var clustering = configuration[OrleansModule.Integration.Key(ClusteringField)];
        var grainState = configuration[OrleansModule.Integration.Key(GrainStateField)];
        var reminders = configuration[OrleansModule.Integration.Key(RemindersField)];
        var fields = new[] { (Field: ClusteringField, Value: clustering), (Field: GrainStateField, Value: grainState), (Field: RemindersField, Value: reminders) };
        var missing = fields.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Field).ToArray();
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

    private static ISiloBuilder AddAzure(ISiloBuilder silo, string clustering, string grainState, string reminders, string? container)
    {
        silo.UseAzureStorageClustering(options => options.TableServiceClient = new TableServiceClient(clustering));
        silo.UseAzureTableReminderService(options => options.TableServiceClient = new TableServiceClient(reminders));
        // The platform's deployment identity and cookie keys live beside the grain state.
        silo.Services.AddKeyedSingleton(DigitalBrainNames.GrainState, (_, _) => new BlobServiceClient(grainState));
        foreach (var name in GrainStores)
        {
            silo.AddAzureBlobGrainStorage(name, options => options.BlobServiceClient = new BlobServiceClient(grainState));
            silo.Services.AddKeyedSingleton<IGrainStorageSerializer>(name, static (services, _) =>
                new OrleansGrainStorageSerializer(services.GetRequiredService<global::Orleans.Serialization.Serializer>()));
            // The integration setting wins; the module option is the deployment default.
            silo.Services.AddOptions<AzureBlobStorageOptions>(name).Configure<IOptions<OrleansOptions>>(
                (options, module) => options.ContainerName = string.IsNullOrWhiteSpace(container) ? module.Value.GrainStateContainer : container);
        }
        return silo;
    }
}
