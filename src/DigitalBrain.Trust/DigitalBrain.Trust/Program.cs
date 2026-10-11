using Azure.Data.Tables;
using Azure.Storage.Blobs;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Platform;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Storage;

namespace DigitalBrain.Trust;

// The trust cluster's host (H2): its own Orleans cluster (no gateway), `platform` storage only, the trust grains,
// and the link endpoint and relay machines reach it through (T20-7); nothing here can address a neuron.
public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // R105: a stop (Ctrl+C/SIGTERM from `aspire stop`) runs the silo's graceful shutdown, which marks it dead in the
        // membership table; RunAsync honours both signals.
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.UseOrleans(silo => Configure(silo, builder.Configuration));
        await builder.Build().RunAsync();
    }

    // The orleans integration's connection strings, read as configuration because the trust host composes no
    // module: with them the cluster lives in Azure tables and blobs, without them in memory on localhost.
    public static ISiloBuilder Configure(ISiloBuilder silo, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(configuration);
        var trust = configuration.GetSection(TrustOptions.Key).Get<TrustOptions>() ?? new TrustOptions();
        trust.Validate();
        var clustering = configuration[IntegrationSchema.Key("orleans", "Clustering")];
        var grainState = configuration[IntegrationSchema.Key("orleans", "GrainState")];
        if (string.IsNullOrWhiteSpace(clustering) || string.IsNullOrWhiteSpace(grainState))
        {
            silo.UseLocalhostClustering(trust.SiloPort, gatewayPort: 0, serviceId: trust.ServiceId, clusterId: trust.ClusterId);
            silo.AddMemoryGrainStorage(DigitalBrainNames.PlatformGrainStorage);
        }
        else
        {
            silo.Configure<ClusterOptions>(options => (options.ClusterId, options.ServiceId) = (trust.ClusterId, trust.ServiceId));
            silo.ConfigureEndpoints(trust.SiloPort, gatewayPort: 0);
            silo.UseAzureStorageClustering(options => options.TableServiceClient = new TableServiceClient(clustering));
            silo.AddAzureBlobGrainStorage(DigitalBrainNames.PlatformGrainStorage, options =>
            {
                options.BlobServiceClient = new BlobServiceClient(grainState);
                options.ContainerName = "trust";
            });
            // The grain serializer, as the machines' storage uses: JSON cannot round-trip every grain state.
            silo.Services.AddKeyedSingleton<IGrainStorageSerializer>(DigitalBrainNames.PlatformGrainStorage, static (services, _) =>
                new OrleansGrainStorageSerializer(services.GetRequiredService<Orleans.Serialization.Serializer>()));
        }
        // A dev trust host on one PC advertises and listens on loopback (R66), its link endpoint and relay too.
        if (trust.AdvertisedAddress is { } advertised)
        {
            var address = System.Net.IPAddress.Parse(advertised);
            silo.Configure<EndpointOptions>(options =>
            {
                options.AdvertisedIPAddress = address;
                options.SiloListeningEndpoint = new System.Net.IPEndPoint(address, trust.SiloPort);
            });
        }
        return silo.AsSingleSiloCluster().AddTrustCluster().AddTrustBoundary().AddTrustLinks();
    }
}
