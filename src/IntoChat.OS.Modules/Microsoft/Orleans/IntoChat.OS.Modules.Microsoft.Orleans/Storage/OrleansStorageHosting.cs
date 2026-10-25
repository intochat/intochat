using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using global::Orleans.Configuration;
using global::Orleans.Hosting;
using global::Orleans.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.Orleans;

public static class OrleansStorageHosting
{
    public const string ClusteringField = OrleansIntegrationNames.Clustering;
    public const string GrainStateField = OrleansIntegrationNames.GrainState;
    public const string RemindersField = OrleansIntegrationNames.Reminders;
    public const string GrainStateContainerField = OrleansIntegrationNames.GrainStateContainer;

    private static readonly string[] GrainStores = [IntoChatOSNames.ClusterGrainStorage, IntoChatOSNames.PlatformGrainStorage];

    // Providers must exist before the silo builds, so the choice is read from configuration here, not
    // from the integration grain at runtime; the Platform seeds the same keys into the integration.
    public static ISiloBuilder AddOrleansStorage(this ISiloBuilder silo, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(configuration);
        var clustering = Read(configuration, ClusteringField);
        var grainState = Read(configuration, GrainStateField);
        var reminders = Read(configuration, RemindersField);
        var fields = new[] { (Field: ClusteringField, Value: clustering), (Field: GrainStateField, Value: grainState), (Field: RemindersField, Value: reminders) };
        var missing = fields.Where(pair => pair.Value is null).Select(pair => pair.Field).ToArray();
        if (missing.Length == fields.Length) { return AddMemory(silo, configuration); }
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"The orleans integration is partial; missing: {string.Join(", ", missing)}. Set all three or none.");
        }
        return AddAzure(silo, configuration, clustering!, grainState!, reminders!, HostConfiguration.IntegrationValue(configuration, OrleansModule.Integration, GrainStateContainerField));
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

    private static ISiloBuilder AddAzure(ISiloBuilder silo, IConfiguration configuration, StorageEndpoint clustering, StorageEndpoint grainState, StorageEndpoint reminders, string? container)
    {
        // Service URIs authenticate with the deployment identity, registered once like the C# module's.
        if (new[] { clustering, grainState, reminders }.Any(endpoint => endpoint.ServiceUri is not null))
        { silo.Services.TryAddSingleton(_ => Credential(configuration)); }
        silo.UseAzureStorageClustering(options => options.Configure<IServiceProvider>((o, services) => o.TableServiceClient = Tables(clustering, services)));
        silo.UseAzureTableReminderService(options => options.Configure<IServiceProvider>((o, services) => o.TableServiceClient = Tables(reminders, services)));
        // The platform's deployment identity and cookie keys live beside the grain state.
        silo.Services.AddKeyedSingleton(IntoChatOSNames.GrainState, (services, _) => grainState.ServiceUri is { } uri
            ? new BlobServiceClient(uri, services.GetRequiredService<TokenCredential>())
            : new BlobServiceClient(grainState.ConnectionString));
        silo.Services.AddKeyedSingleton<BlobContainerClient>(IntoChatOSNames.GrainState, (services, _) =>
            services.GetRequiredKeyedService<BlobServiceClient>(IntoChatOSNames.GrainState).GetBlobContainerClient(
                services.GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>().Get(IntoChatOSNames.PlatformGrainStorage).ContainerName));
        foreach (var name in GrainStores)
        {
            silo.AddAzureBlobGrainStorage(name, options => options.Configure<IServiceProvider>((o, services) =>
                o.BlobServiceClient = services.GetRequiredKeyedService<BlobServiceClient>(IntoChatOSNames.GrainState)));
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

    private static StorageEndpoint? Read(IConfiguration configuration, string field) => StorageEndpoint.Select(field,
        HostConfiguration.IntegrationValue(configuration, OrleansModule.Integration, field),
        HostConfiguration.IntegrationValue(configuration, OrleansModule.Integration, field + StorageEndpoint.UriSuffix));

    private static TokenCredential Credential(IConfiguration configuration) => new DefaultAzureCredential(StorageEndpoint.CredentialOptions(
        HostConfiguration.IntegrationValue(configuration, OrleansModule.Integration, StorageEndpoint.ManagedIdentityClientIdField),
        HostConfiguration.Variable(configuration, StorageEndpoint.ClientIdVariable) ?? Environment.GetEnvironmentVariable(StorageEndpoint.ClientIdVariable)));

    // An Orleans client (the Aspire test session) joins clustering by the silo's rule: the connection
    // it was handed, or the ClusteringUri setting, with the deployment identity for a service URI.
    public static TableServiceClient ClusteringClient(IConfiguration configuration, string? connection)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = StorageEndpoint.Select(ClusteringField, connection,
            HostConfiguration.IntegrationValue(configuration, OrleansModule.Integration, ClusteringField + StorageEndpoint.UriSuffix))
            ?? throw new InvalidOperationException("The orleans clustering store is not configured.");
        return endpoint.ServiceUri is { } uri ? new TableServiceClient(uri, Credential(configuration)) : new TableServiceClient(endpoint.ConnectionString);
    }
}
