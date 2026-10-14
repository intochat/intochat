using DigitalBrain.Microsoft.Orleans;
using Azure.Storage.Blobs;
using DigitalBrain.Framework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Hosting;
using Orleans.Storage;

namespace DigitalBrain.Modules.Microsoft.Orleans.Tests;

public sealed class StorageSelectionFacts
{
    [Fact]
    public void AnAbsentIntegrationSelectsMemoryStores()
    {
        var services = new ServiceCollection();
        var silo = new FakeSilo(services, new ConfigurationBuilder().Build());
        silo.AddOrleansStorage(silo.Configuration);
        Assert.Contains(services, d => d.ServiceType == typeof(MemoryStores));
        Assert.Contains(services, d => d.ServiceType == typeof(IGrainStorage) && Equals(d.ServiceKey, "cluster"));
        Assert.Contains(services, d => d.ServiceType == typeof(IGrainStorage) && Equals(d.ServiceKey, "platform"));
    }

    [Fact]
    public void APartialIntegrationRefusesToStartNamingTheMissingFields()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OrleansModule.Integration.Key(OrleansStorageHosting.ClusteringField)] = "UseDevelopmentStorage=true",
        }).Build();
        var silo = new FakeSilo(new ServiceCollection(), configuration);
        var error = Assert.Throws<InvalidOperationException>(() => silo.AddOrleansStorage(configuration));
        Assert.Contains("GrainState", error.Message, StringComparison.Ordinal);
        Assert.Contains("Reminders", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("from-integration", "from-option", "from-integration")]
    [InlineData(null, "from-option", "from-option")]
    public void TheIntegrationGrainStateContainerWinsOverTheModuleOption(string? integration, string option, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OrleansModule.Integration.Key(OrleansStorageHosting.ClusteringField)] = "UseDevelopmentStorage=true",
            [OrleansModule.Integration.Key(OrleansStorageHosting.GrainStateField)] = "UseDevelopmentStorage=true",
            [OrleansModule.Integration.Key(OrleansStorageHosting.RemindersField)] = "UseDevelopmentStorage=true",
            [OrleansModule.Integration.Key(OrleansStorageHosting.GrainStateContainerField)] = integration,
        }).Build();
        var services = new ServiceCollection();
        services.AddSerializer();
        services.Configure<OrleansOptions>(options => options.GrainStateContainer = option);
        new FakeSilo(services, configuration).AddOrleansStorage(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(expected, provider.GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>().Get("cluster").ContainerName);
        Assert.Equal(expected, provider.GetRequiredKeyedService<BlobContainerClient>(DigitalBrainNames.GrainState).Name);
    }

    private sealed class FakeSilo(IServiceCollection services, IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = services;
        public IConfiguration Configuration { get; } = configuration;
    }
}
