using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using static IntoChat.OS.Aspire.Testing.AspireCompositionTest;

namespace IntoChat.OS.Modules.Microsoft.Orleans.Tests.Hosting;

public sealed class OrleansHostingFacts
{
    private static readonly string IntegrationPrefix = IntoChatOSConfiguration.IntegrationEnvironmentName("orleans", "");

    [Fact]
    public async Task WithAzuriteTheSiloRunsTheCloudTopologyOverTheEmulatorAccount()
    {
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("Modules", new())
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var silo = builder.AddExecutable("os", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, silo.Resource, TestContext.Current.CancellationToken);
        Assert.Equal(IntoChatOSNames.TopologyCloud, environment[IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.TopologyKey)]);
        Assert.Contains(IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.CloudStorageConfigurationKey), environment.Keys);
        // No Key Vault emulator: none is projected unless the AppHost configuration names one.
        Assert.DoesNotContain(IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.CloudKeyVaultConfigurationKey), environment.Keys);
        Assert.DoesNotContain(environment.Keys, key => key.StartsWith(IntegrationPrefix, StringComparison.Ordinal));
        AssertNoProviderKeys(environment);
        Assert.Contains("Orleans__ClusterId", environment.Keys);
        Assert.Single(builder.Resources.OfType<AzureStorageResource>());
        Assert.Contains(silo.Resource.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource is AzureStorageResource);
    }

    [Fact]
    public async Task WithAzuriteAConfiguredVaultIsPassedThrough()
    {
        var builder = CreateBuilder();
        builder.Configuration[IntoChatOSNames.CloudKeyVaultConfigurationKey] = "https://dev-vault.vault.azure.net/";
        var brain = builder.AddIntoChatOS("Modules", new())
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var silo = builder.AddExecutable("os", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, silo.Resource, TestContext.Current.CancellationToken);
        Assert.Equal("https://dev-vault.vault.azure.net/", environment[IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.CloudKeyVaultConfigurationKey)]);
    }

    [Fact]
    public async Task WithoutAzuriteTheSiloGetsNoCloudKeys()
    {
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("Modules").WithHosting<OrleansModuleHosting, OrleansOptions>(_ => { });
        var silo = builder.AddExecutable("os", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, silo.Resource, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(environment.Keys, key => key.StartsWith(IntegrationPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.CloudStorageConfigurationKey), environment.Keys);
        Assert.DoesNotContain(IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.CloudKeyVaultConfigurationKey), environment.Keys);
        Assert.NotEqual(IntoChatOSNames.TopologyCloud, environment.GetValueOrDefault(IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.TopologyKey)));
        AssertNoProviderKeys(environment);
        Assert.Empty(builder.Resources.OfType<AzureStorageResource>());
    }

    [Fact]
    public async Task TheSiloReadsTheDeploymentGrainStateContainerAsTheOrleansModuleOption()
    {
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("Modules", new() { GrainStateContainer = "brain-rabc1234" })
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var silo = builder.AddExecutable("os", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, silo.Resource, TestContext.Current.CancellationToken);
        Assert.Equal("brain-rabc1234", environment[IntoChatOSConfiguration.EnvironmentName(IntoChatOSConfiguration.ModuleOptionsSection("orleans"), nameof(OrleansOptions.GrainStateContainer))]);
    }

    [Fact]
    public async Task AnOrleansClientFindsTheAzuriteClusteringTable()
    {
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("Modules", new())
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var client = builder.AddExecutable("mcp", "unused", ".").WithReference(brain.AsClient());
        var environment = await EnvironmentOf(builder, client.Resource, TestContext.Current.CancellationToken);
        Assert.Equal("AzureTableStorage", environment["Orleans__Clustering__ProviderType"]);
        Assert.Equal(IntoChat.OS.Contracts.Configuration.IntoChatOSNames.Clustering, environment["Orleans__Clustering__ServiceKey"]);
    }

    private static void AssertNoProviderKeys(Dictionary<string, object> environment)
    {
        Assert.DoesNotContain("Orleans__Clustering__ProviderType", environment.Keys);
        Assert.DoesNotContain(environment.Keys, key => key.StartsWith("Orleans__Clustering__", StringComparison.Ordinal)
            || key.StartsWith("Orleans__Reminders__", StringComparison.Ordinal) || key.StartsWith("Orleans__GrainStorage__", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hosting_adapter_claims_its_module_through_the_generic_parameter()
        => Assert.Equal("orleans", new OrleansModuleHosting().Module.ToString());
}
