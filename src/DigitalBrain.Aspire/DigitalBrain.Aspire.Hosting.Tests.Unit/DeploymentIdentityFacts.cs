using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Microsoft.Orleans;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class DeploymentIdentityFacts
{
    [Fact]
    public async Task RuntimeAndHttpClientReceiveTheSameOverriddenSessionIdentity()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.CookieName))] = "custom.session";
        builder.Configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionApplicationName))] = "IntoChat.v1";
        builder.Configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionContainerName))] = "intochat-protection-v1";
        var brain = builder.AddDigitalBrain("memory", options: new())
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite())
            .WithHttpIdentity();
        var server = builder.AddExecutable("server", "unused", ".").WithReference(brain);
        var client = builder.AddExecutable("mcp", "unused", ".").WithReference(brain.AsClient(shareHttpIdentity: true));
        foreach (var resource in new[] { server.Resource, client.Resource })
        {
            var values = new Dictionary<string, object>();
            var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, values, TestContext.Current.CancellationToken);
            foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await annotation.Callback(context); }
            Assert.Equal("custom.session", values[DigitalBrainConfiguration.EnvironmentName(IdentityHostOptions.Key(nameof(IdentityHostOptions.CookieName)))]);
            Assert.Equal("IntoChat.v1", values[DigitalBrainConfiguration.EnvironmentName(IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionApplicationName)))]);
            Assert.Equal("intochat-protection-v1", values[DigitalBrainConfiguration.EnvironmentName(IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionContainerName)))]);
            Assert.Contains("ConnectionStrings__" + DigitalBrainNames.GrainState, values.Keys);
        }
    }

    [Theory]
    [InlineData(null, "Open")]
    [InlineData("Secured", "Secured")]
    public async Task ClientsReceiveTheDeclaredAuthPosture(string? configured, string expected)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        if (configured is not null) { builder.Configuration[AuthOptions.PostureKey] = configured; }
        var brain = builder.AddDigitalBrain("memory");
        var client = builder.AddExecutable("client", "unused", ".").WithReference(brain.AsClient());
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, client.Resource, values, TestContext.Current.CancellationToken);
        foreach (var annotation in client.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        { await annotation.Callback(context); }
        Assert.Equal(expected, values[DigitalBrainConfiguration.EnvironmentName(AuthOptions.PostureKey)]);
    }

    [Fact]
    public void BlobContainerIdentityDoesNotInheritTheAspireResourcePrefix()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var original = builder.AddDigitalBrain("Modules", options: new() { PersistentStorage = false })
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite())
            .AddBlobContainer("cookie-protection");
        var renamed = builder.AddDigitalBrain("Renamed", options: new() { PersistentStorage = false })
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite())
            .AddBlobContainer("cookie-protection");
        Assert.Equal("Modules-cookie-protection", original.Resource.Name);
        Assert.Equal("Renamed-cookie-protection", renamed.Resource.Name);
        Assert.Equal("cookie-protection", original.Resource.BlobContainerName);
        Assert.Equal(original.Resource.BlobContainerName, renamed.Resource.BlobContainerName);
    }

    [Fact]
    public async Task LocalDevelopmentSiloBindsTheAdvertisedLocalhostGateway()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var server = builder.AddExecutable("server", "unused", ".").WithReference(builder.AddDigitalBrain("memory"));
        var configuration = await ExecutionConfigurationBuilder.Create(server.Resource).WithEnvironmentVariablesConfig()
            .AddExecutionConfigurationGatherer(new ServiceKeys())
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.Equal("127.0.0.1", configuration.EnvironmentVariables.ToDictionary()["Orleans__Endpoints__AdvertisedIPAddress"]);
        Assert.All(server.Resource.Annotations.OfType<EndpointAnnotation>(), endpoint => Assert.False(endpoint.IsProxied));
    }

    [Fact]
    public async Task TheSiloReadsTheOrleansIntegrationAndClientsAStableClusteringServiceKey()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("renamed", options: new()).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var server = builder.AddExecutable("server", "unused", ".").WithReference(brain);
        var client = builder.AddExecutable("client", "unused", ".").WithReference(brain.AsClient());
        foreach (var resource in new[] { server.Resource, client.Resource })
        {
            var configuration = await ExecutionConfigurationBuilder.Create(resource).WithEnvironmentVariablesConfig()
                .AddExecutionConfigurationGatherer(new ServiceKeys())
                .BuildAsync(builder.ExecutionContext, NullLogger.Instance, TestContext.Current.CancellationToken);
            var keys = configuration.EnvironmentVariables.ToDictionary();
            if (resource == server.Resource)
            {
                Assert.DoesNotContain(keys.Keys, key => key.StartsWith("Orleans__", StringComparison.Ordinal));
                var environment = await DigitalBrain.Aspire.Testing.AspireCompositionTest.EnvironmentOf(builder, resource, TestContext.Current.CancellationToken);
                foreach (var field in new[] { OrleansStorageHosting.ClusteringField, OrleansStorageHosting.GrainStateField, OrleansStorageHosting.RemindersField })
                { Assert.Contains(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", field), environment.Keys); }
            }
            else { Assert.Equal(DigitalBrainNames.Clustering, keys["Orleans__Clustering__ServiceKey"]); }
        }
    }

    [Fact]
    public void ASiloHostWaitsForAGracefulLeaveOnStop()
    {
        var host = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        host.Configuration[AuthOptions.PostureKey] = "Open";
        DigitalBrain.Aspire.Server.ServerHostingExtensions.AddDigitalBrainServer(host, server => server.UseAzureStorage());
        using var provider = host.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<HostOptions>>().Get(null);
        Assert.Equal(TimeSpan.FromSeconds(40), options.ShutdownTimeout);
    }

    private sealed class ServiceKeys : IExecutionConfigurationGatherer
    {
        public ValueTask GatherAsync(IExecutionConfigurationGathererContext context, IResource resource,
            ILogger resourceLogger, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken = default)
        {
            foreach (var key in context.EnvironmentVariables.Keys.Where(k => !k.EndsWith("__ServiceKey", StringComparison.Ordinal)
                && k != "Orleans__Endpoints__AdvertisedIPAddress").ToArray())
            { context.EnvironmentVariables.Remove(key); }
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void ClusterChangesDoNotMovePersistentState()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[DigitalBrainHostingOptions.ClusterIdKey] = "new-run";
        var brain = builder.AddDigitalBrain("renamed-resource", new DigitalBrainHostingOptions { ServiceId = "existing-data" });
        Assert.Equal("existing-data", brain.ServiceId);
        Assert.Equal("new-run", brain.ClusterId);
        Assert.Equal("default-service", builder.AddDigitalBrain("default-service").ServiceId);
    }

    [Fact]
    public void ServiceIdAndClusterIdComeFromConfigurationWithTheIntoChatDefault()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var defaults = builder.AddDigitalBrain("Modules", DigitalBrainHostingOptions.Named(builder.Configuration, "intochat"));
        Assert.Equal("intochat", defaults.ServiceId);
        Assert.Equal("intochat", defaults.ClusterId);
        Assert.Equal(DigitalBrainNames.GrainStateContainer, defaults.GrainStateContainer);

        builder.Configuration[DigitalBrainHostingOptions.ServiceIdKey] = "intochat-rabc1234";
        builder.Configuration[DigitalBrainHostingOptions.ClusterIdKey] = "intochat-rabc1234";
        builder.Configuration[DigitalBrainHostingOptions.GrainStateContainerKey] = "brain-rabc1234";
        var revision = builder.AddDigitalBrain("Revision", DigitalBrainHostingOptions.Named(builder.Configuration, "intochat"));
        Assert.Equal("intochat-rabc1234", revision.ServiceId);
        Assert.Equal("intochat-rabc1234", revision.ClusterId);
        Assert.Equal("brain-rabc1234", revision.GrainStateContainer);
    }

    [Fact]
    public void ADevelopmentRunGetsAClusterOfItsOwnUnderTheStableServiceId()
    {
        var first = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = ["--environment", "Development"], DisableDashboard = true });
        var second = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = ["--environment", "Development"], DisableDashboard = true });

        var one = first.AddDigitalBrain("Modules", DigitalBrainHostingOptions.Named(first.Configuration, "intochat"));
        var two = second.AddDigitalBrain("Modules", DigitalBrainHostingOptions.Named(second.Configuration, "intochat"));

        Assert.Equal(("intochat", "intochat"), (one.ServiceId, two.ServiceId));
        Assert.NotEqual(one.ClusterId, two.ClusterId);
    }

    [Fact]
    public void TheGrainStateContainerComesFromConfigurationWithTheV2Default()
    {
        var host = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        host.Configuration[DigitalBrainNames.GrainStateContainerKey] = "brain-rabc1234";
        host.Configuration[AuthOptions.PostureKey] = "Open";
        DigitalBrain.Aspire.Server.ServerHostingExtensions.AddDigitalBrainServer(host, server => server.UseAzureStorage());
        using var provider = host.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<Orleans.Configuration.AzureBlobStorageOptions>>().Get(DigitalBrainNames.ClusterGrainStorage);
        Assert.Equal("brain-rabc1234", options.ContainerName);
    }

    [Fact]
    public void MultipleServersRequireExplicitSelectionAndResolveTheirOwnBrain()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var first = builder.AddDigitalBrain("first", new DigitalBrainHostingOptions { ServiceId = "first-data", ClusterId = "first-run" }).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var second = builder.AddDigitalBrain("second", new DigitalBrainHostingOptions { ServiceId = "second-data", ClusterId = "second-run" }).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        builder.AddExecutable("first-server", "dotnet", ".").WithReference(first);
        builder.AddExecutable("second-server", "dotnet", ".").WithReference(second);
        Assert.Throws<InvalidOperationException>(() => SiloHosts.Select(builder.Resources));
        var selected = SiloHosts.BrainOf(SiloHosts.Select(builder.Resources, "second-server"));
        Assert.Same(second, selected);
        Assert.Equal("second-data", selected.ServiceId);
        Assert.Equal("second-run", selected.ClusterId);
        Assert.Equal(second.ResourceName(DigitalBrainNames.Clustering), selected.ClusteringResourceName);
    }
}
