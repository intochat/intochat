using Azure.Storage.Blobs;
using IntoChat.OS.Framework;
using IntoChat.OS.Microsoft.Orleans;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Modules.Microsoft.Orleans.Tests.Integration;

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
        Assert.Equal(expected, provider.GetRequiredKeyedService<BlobContainerClient>(IntoChatOSNames.GrainState).Name);
    }

    [Fact]
    public void ServiceUrisUseTheDeploymentIdentityForEveryStore()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OrleansModule.Integration.Key(OrleansStorageHosting.ClusteringField)] = "https://st.table.core.windows.net/",
            [OrleansModule.Integration.Key(OrleansStorageHosting.GrainStateField + "Uri")] = "https://st.blob.core.windows.net/",
            [OrleansModule.Integration.Key(OrleansStorageHosting.RemindersField + "Uri")] = "https://st.table.core.windows.net/",
        }).Build();
        var services = new ServiceCollection();
        services.AddSerializer();
        new FakeSilo(services, configuration).AddOrleansStorage(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<Azure.Identity.DefaultAzureCredential>(provider.GetRequiredService<Azure.Core.TokenCredential>());
        var blobs = provider.GetRequiredKeyedService<BlobServiceClient>(IntoChatOSNames.GrainState);
        Assert.Equal("st", blobs.AccountName);
        Assert.Same(blobs, provider.GetRequiredService<IOptionsMonitor<AzureBlobStorageOptions>>().Get("platform").BlobServiceClient);
        Assert.Equal("st", provider.GetRequiredService<IOptions<global::Orleans.Clustering.AzureStorage.AzureStorageClusteringOptions>>().Value.TableServiceClient!.AccountName);
    }

    [Fact]
    public void ConnectionStringsRegisterNoCredential()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OrleansModule.Integration.Key(OrleansStorageHosting.ClusteringField)] = "UseDevelopmentStorage=true",
            [OrleansModule.Integration.Key(OrleansStorageHosting.GrainStateField)] = "UseDevelopmentStorage=true",
            [OrleansModule.Integration.Key(OrleansStorageHosting.RemindersField)] = "UseDevelopmentStorage=true",
        }).Build();
        var services = new ServiceCollection();
        new FakeSilo(services, configuration).AddOrleansStorage(configuration);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Azure.Core.TokenCredential));
    }

    [Fact]
    public void AStoreWithBothAConnectionStringAndAUriRefusesToStart()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OrleansModule.Integration.Key(OrleansStorageHosting.GrainStateField)] = "UseDevelopmentStorage=true",
            [OrleansModule.Integration.Key(OrleansStorageHosting.GrainStateField + "Uri")] = "https://st.blob.core.windows.net/",
        }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => new FakeSilo(new ServiceCollection(), configuration).AddOrleansStorage(configuration));
        Assert.Contains("GrainStateUri", error.Message, StringComparison.Ordinal);
    }

    private sealed class FakeSilo(IServiceCollection services, IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = services;
        public IConfiguration Configuration { get; } = configuration;
    }
}
