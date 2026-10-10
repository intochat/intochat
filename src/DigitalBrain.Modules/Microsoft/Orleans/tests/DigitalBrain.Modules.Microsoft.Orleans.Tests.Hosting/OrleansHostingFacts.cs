using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Microsoft.Orleans;
using static DigitalBrain.Aspire.Testing.AspireCompositionTest;

namespace DigitalBrain.Modules.Microsoft.Orleans.Tests.Hosting;

public sealed class OrleansHostingFacts
{
    private static readonly string IntegrationPrefix = DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", "");

    [Fact]
    public async Task WithAzuriteProjectsTheThreeConnectionsIntoTheOrleansIntegration()
    {
        var builder = CreateBuilder();
        var brain = builder.AddDigitalBrain("Modules", new() { PersistentStorage = false })
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var silo = builder.AddExecutable("os", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, silo.Resource, TestContext.Current.CancellationToken);
        Assert.Contains(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", OrleansStorageHosting.ClusteringField), environment.Keys);
        Assert.Contains(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", OrleansStorageHosting.GrainStateField), environment.Keys);
        Assert.Contains(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", OrleansStorageHosting.RemindersField), environment.Keys);
        // The silo keeps only the development clustering Aspire's Orleans resource requires; no storage provider keys.
        Assert.DoesNotContain("Orleans__Clustering__ServiceKey", environment.Keys);
        Assert.Equal("Development", environment["Orleans__Clustering__ProviderType"]);
        Assert.DoesNotContain(environment.Keys, key => key.StartsWith("Orleans__Reminders__", StringComparison.Ordinal)
            || key.StartsWith("Orleans__GrainStorage__", StringComparison.Ordinal));
        Assert.Single(builder.Resources.OfType<AzureStorageResource>());
        Assert.Contains(silo.Resource.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource is AzureStorageResource);
    }

    [Fact]
    public async Task WithoutAzuriteTheSiloGetsNoOrleansIntegration()
    {
        var builder = CreateBuilder();
        var brain = builder.AddDigitalBrain("Modules").WithHosting<OrleansModuleHosting, OrleansOptions>(_ => { });
        var silo = builder.AddExecutable("os", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, silo.Resource, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(environment.Keys, key => key.StartsWith(IntegrationPrefix, StringComparison.Ordinal));
        Assert.Empty(builder.Resources.OfType<AzureStorageResource>());
    }

    [Fact]
    public async Task TheSiloReadsTheDeploymentGrainStateContainerAsTheOrleansModuleOption()
    {
        var builder = CreateBuilder();
        var brain = builder.AddDigitalBrain("Modules", new() { GrainStateContainer = "brain-rabc1234", PersistentStorage = false })
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var silo = builder.AddExecutable("os", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, silo.Resource, TestContext.Current.CancellationToken);
        Assert.Equal("brain-rabc1234", environment[DigitalBrainConfiguration.EnvironmentName(DigitalBrainConfiguration.ModuleOptionsSection("orleans"), nameof(OrleansOptions.GrainStateContainer))]);
    }

    [Fact]
    public async Task AnOrleansClientFindsTheAzuriteClusteringTable()
    {
        var builder = CreateBuilder();
        var brain = builder.AddDigitalBrain("Modules", new() { PersistentStorage = false })
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var client = builder.AddExecutable("mcp", "unused", ".").WithReference(brain.AsClient());
        var environment = await EnvironmentOf(builder, client.Resource, TestContext.Current.CancellationToken);
        Assert.Equal("AzureTableStorage", environment["Orleans__Clustering__ProviderType"]);
        Assert.Equal(DigitalBrain.Framework.DigitalBrainNames.Clustering, environment["Orleans__Clustering__ServiceKey"]);
    }

    [Fact]
    public void A_hosting_adapter_claims_its_module_through_the_generic_parameter()
        => Assert.Equal("orleans", new OrleansModuleHosting().Module.ToString());
}
