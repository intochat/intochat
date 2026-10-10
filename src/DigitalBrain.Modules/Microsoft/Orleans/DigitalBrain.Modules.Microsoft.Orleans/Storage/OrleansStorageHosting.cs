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
        if (missing.Length == fields.Length) { return AddMemory(silo); }
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"The orleans integration is partial; missing: {string.Join(", ", missing)}. Set all three or none.");
        }
        return AddAzure(silo, clustering!, grainState!, reminders!);
    }

    private static ISiloBuilder AddMemory(ISiloBuilder silo)
    {
        silo.Services.TryAddSingleton<MemoryStores>();
        silo.UseLocalhostClustering();
        silo.UseInMemoryReminderService();
        foreach (var name in GrainStores)
        {
            silo.Services.AddKeyedSingleton<IGrainStorage>(name, static (provider, key) => new MemoryStoreGrainStorage(
                provider.GetRequiredService<MemoryStores>().Store((string)key!),
                provider.GetRequiredService<global::Orleans.Serialization.Serializer>()));
        }
        return silo;
    }

    private static ISiloBuilder AddAzure(ISiloBuilder silo, string clustering, string grainState, string reminders)
    {
        silo.UseAzureStorageClustering(options => options.TableServiceClient = new TableServiceClient(clustering));
        silo.UseAzureTableReminderService(options => options.TableServiceClient = new TableServiceClient(reminders));
        foreach (var name in GrainStores)
        {
            silo.AddAzureBlobGrainStorage(name, options => options.BlobServiceClient = new BlobServiceClient(grainState));
            silo.Services.AddKeyedSingleton<IGrainStorageSerializer>(name, static (services, _) =>
                new OrleansGrainStorageSerializer(services.GetRequiredService<global::Orleans.Serialization.Serializer>()));
            silo.Services.AddOptions<AzureBlobStorageOptions>(name).Configure<IOptions<OrleansOptions>>(
                (options, module) => options.ContainerName = module.Value.GrainStateContainer);
        }
        return silo;
    }
}
