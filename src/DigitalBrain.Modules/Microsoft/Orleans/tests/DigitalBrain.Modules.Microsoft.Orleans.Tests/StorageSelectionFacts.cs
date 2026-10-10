using DigitalBrain.Microsoft.Orleans;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    private sealed class FakeSilo(IServiceCollection services, IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = services;
        public IConfiguration Configuration { get; } = configuration;
    }
}
