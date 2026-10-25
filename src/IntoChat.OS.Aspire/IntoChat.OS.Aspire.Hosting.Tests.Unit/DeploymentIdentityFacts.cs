using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Modules.Microsoft.Orleans;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class DeploymentIdentityFacts
{
    [Fact]
    public async Task RuntimeAndHttpClientReceiveTheSameOverriddenSessionIdentity()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.CookieName))] = "custom.session";
        builder.Configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionApplicationName))] = "IntoChat.v1";
        builder.Configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionContainerName))] = "intochat-protection-v1";
        var brain = builder.AddIntoChatOS("memory", options: new())
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite())
            .WithHttpIdentity();
        var server = builder.AddExecutable("server", "unused", ".").WithReference(brain);
        var client = builder.AddExecutable("mcp", "unused", ".").WithReference(brain.AsClient(shareHttpIdentity: true));
        foreach (var resource in new[] { server.Resource, client.Resource })
        {
            var values = new Dictionary<string, object>();
            var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, values, TestContext.Current.CancellationToken);
            foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await annotation.Callback(context); }
            Assert.Equal("custom.session", values[IntoChatOSConfiguration.EnvironmentName(IdentityHostOptions.Key(nameof(IdentityHostOptions.CookieName)))]);
            Assert.Equal("IntoChat.v1", values[IntoChatOSConfiguration.EnvironmentName(IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionApplicationName)))]);
            Assert.Equal("intochat-protection-v1", values[IntoChatOSConfiguration.EnvironmentName(IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionContainerName)))]);
            // The silo shares the client's cookie key store through the orleans integration; the client by reference.
            Assert.Contains(resource == server.Resource
                ? IntoChatOSConfiguration.IntegrationEnvironmentName("orleans", OrleansStorageHosting.GrainStateField)
                : "ConnectionStrings__" + IntoChatOSNames.GrainState, values.Keys);
        }
    }

    [Theory]
    [InlineData(null, "Open")]
    [InlineData("Secured", "Secured")]
    public async Task ClientsReceiveTheDeclaredAuthPosture(string? configured, string expected)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        if (configured is not null) { builder.Configuration[AuthOptions.PostureKey] = configured; }
        var brain = builder.AddIntoChatOS("memory");
        var client = builder.AddExecutable("client", "unused", ".").WithReference(brain.AsClient());
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, client.Resource, values, TestContext.Current.CancellationToken);
        foreach (var annotation in client.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        { await annotation.Callback(context); }
        Assert.Equal(expected, values[IntoChatOSConfiguration.EnvironmentName(AuthOptions.PostureKey)]);
    }

    // The server has no Aspire execution context: an Aspire run gives the processes it starts the local topology so Open
    // posture holds them to loopback (OpenPostureLoopback, BindPolicy). Publish mode is out of scope and projects no topology.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RunModeGivesTheKernelAndMcpResourcesTheLocalTopology(bool publish, bool marked)
    {
        var output = Directory.CreateTempSubdirectory("publish-").FullName;
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = publish ? ["--operation", "publish", "--output-path", output] : [],
            DisableDashboard = true,
        });
        Assert.Equal(publish, builder.ExecutionContext.IsPublishMode);
        builder.Configuration[AuthOptions.PostureKey] = "Open";
        var brain = builder.AddIntoChatOS("memory");
        var kernel = builder.AddExecutable("kernel", "unused", ".").WithReference(brain);
        var mcp = builder.AddExecutable("mcp", "unused", ".").WithReference(brain.AsClient());
        foreach (var resource in new[] { kernel.Resource, mcp.Resource })
        {
            var values = new Dictionary<string, object>();
            var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, values, TestContext.Current.CancellationToken);
            foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await annotation.Callback(context); }
            var key = IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.TopologyKey);
            Assert.Equal(marked, values.ContainsKey(key));
            if (marked) { Assert.Equal(IntoChatOSNames.TopologyLocal, values[key]); }
            Assert.DoesNotContain(values.Keys, name => IntoChat.OS.ServiceDefaults.LegacyConfigurationGuard.RetiredSettings.Any(row => IntoChatOSConfiguration.EnvironmentName(row.Name) == name));
        }
    }

    [Fact]
    public async Task SecuredHostsReceiveTheConfiguredBootstrapCredential()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Username))] = "owner";
        builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Password))] = "test-only-bootstrap-password";
        var brain = builder.AddIntoChatOS("memory");
        var server = builder.AddExecutable("server", "unused", ".").WithReference(brain);
        var client = builder.AddExecutable("client", "unused", ".").WithReference(brain.AsClient());
        foreach (var resource in new[] { server.Resource, client.Resource })
        {
            var values = new Dictionary<string, object>();
            var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, values, TestContext.Current.CancellationToken);
            foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            { await annotation.Callback(context); }
            Assert.Equal("owner", values[IntoChatOSConfiguration.EnvironmentName(AuthOptions.Key(nameof(AuthOptions.Username)))]);
            var password = Assert.IsType<ParameterResource>(values[IntoChatOSConfiguration.EnvironmentName(AuthOptions.Key(nameof(AuthOptions.Password)))]);
            Assert.True(password.Secret);
            Assert.Null(password.Default);
            Assert.Equal("{intochat-os-bootstrap-password.value}", password.ValueExpression);
            Assert.Equal("test-only-bootstrap-password", await ((IValueProvider)password).GetValueAsync(TestContext.Current.CancellationToken));
            Assert.Same(Assert.Single(builder.Resources.OfType<ParameterResource>(), parameter => parameter.Name == "intochat-os-bootstrap-password"), password);
        }
    }

    [Theory]
    [InlineData("owner", null)]
    [InlineData(null, "test-only-password")]
    public void IncompleteBootstrapCredentialsAreRejected(string? username, string? password)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[AuthOptions.PostureKey] = "Secured";
        builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Username))] = username;
        builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Password))] = password;
        Assert.Throws<InvalidOperationException>(() => builder.AddIntoChatOS("memory"));
    }
    [Fact]
    public void BlobContainerIdentityDoesNotInheritTheAspireResourcePrefix()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var original = builder.AddIntoChatOS("Modules", options: new())
            .WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite())
            .AddBlobContainer("cookie-protection");
        var renamed = builder.AddIntoChatOS("Renamed", options: new())
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
        var server = builder.AddExecutable("server", "unused", ".").WithReference(builder.AddIntoChatOS("memory"));
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
        var brain = builder.AddIntoChatOS("renamed", options: new()).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
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
                var environment = await IntoChat.OS.Aspire.Testing.AspireCompositionTest.EnvironmentOf(builder, resource, TestContext.Current.CancellationToken);
                foreach (var field in new[] { OrleansStorageHosting.ClusteringField, OrleansStorageHosting.GrainStateField, OrleansStorageHosting.RemindersField })
                { Assert.Contains(IntoChatOSConfiguration.IntegrationEnvironmentName("orleans", field), environment.Keys); }
            }
            else { Assert.Equal(IntoChatOSNames.Clustering, keys["Orleans__Clustering__ServiceKey"]); }
        }
    }

    [Fact]
    public void ASiloHostWaitsForAGracefulLeaveOnStop()
    {
        var host = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        host.Configuration[AuthOptions.PostureKey] = "Open";
        IntoChat.OS.Aspire.Server.ServerHostingExtensions.AddIntoChatOSServer(host, _ => { });
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
        builder.Configuration[IntoChatOSHostingOptions.ClusterIdKey] = "new-run";
        var brain = builder.AddIntoChatOS("renamed-resource", new IntoChatOSHostingOptions { ServiceId = "existing-data" });
        Assert.Equal("existing-data", brain.ServiceId);
        Assert.Equal("new-run", brain.ClusterId);
        Assert.Equal("default-service", builder.AddIntoChatOS("default-service").ServiceId);
    }

    [Fact]
    public void ServiceIdAndClusterIdComeFromConfigurationWithTheIntoChatDefault()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var defaults = builder.AddIntoChatOS("Modules", IntoChatOSHostingOptions.Named(builder.Configuration, "intochat"));
        Assert.Equal("intochat", defaults.ServiceId);
        Assert.Equal("intochat", defaults.ClusterId);
        Assert.Equal(IntoChatOSNames.GrainStateContainer, defaults.GrainStateContainer);

        builder.Configuration[IntoChatOSHostingOptions.ServiceIdKey] = "intochat-rabc1234";
        builder.Configuration[IntoChatOSHostingOptions.ClusterIdKey] = "intochat-rabc1234";
        builder.Configuration[IntoChatOSHostingOptions.GrainStateContainerKey] = "brain-rabc1234";
        var revision = builder.AddIntoChatOS("Revision", IntoChatOSHostingOptions.Named(builder.Configuration, "intochat"));
        Assert.Equal("intochat-rabc1234", revision.ServiceId);
        Assert.Equal("intochat-rabc1234", revision.ClusterId);
        Assert.Equal("brain-rabc1234", revision.GrainStateContainer);
    }

    [Fact]
    public void ADevelopmentRunGetsAClusterOfItsOwnUnderTheStableServiceId()
    {
        var first = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = ["--environment", "Development"], DisableDashboard = true });
        var second = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = ["--environment", "Development"], DisableDashboard = true });

        var one = first.AddIntoChatOS("Modules", IntoChatOSHostingOptions.Named(first.Configuration, "intochat"));
        var two = second.AddIntoChatOS("Modules", IntoChatOSHostingOptions.Named(second.Configuration, "intochat"));

        Assert.Equal(("intochat", "intochat"), (one.ServiceId, two.ServiceId));
        Assert.NotEqual(one.ClusterId, two.ClusterId);
    }

    [Fact]
    public void MultipleServersRequireExplicitSelectionAndResolveTheirOwnBrain()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var first = builder.AddIntoChatOS("first", new IntoChatOSHostingOptions { ServiceId = "first-data", ClusterId = "first-run" }).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var second = builder.AddIntoChatOS("second", new IntoChatOSHostingOptions { ServiceId = "second-data", ClusterId = "second-run" }).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        builder.AddExecutable("first-server", "dotnet", ".").WithReference(first);
        builder.AddExecutable("second-server", "dotnet", ".").WithReference(second);
        Assert.Throws<InvalidOperationException>(() => SiloHosts.Select(builder.Resources));
        var selected = SiloHosts.BrainOf(SiloHosts.Select(builder.Resources, "second-server"));
        Assert.Same(second, selected);
        Assert.Equal("second-data", selected.ServiceId);
        Assert.Equal("second-run", selected.ClusterId);
        Assert.Equal(second.ResourceName(IntoChatOSNames.Clustering), selected.ClusteringResourceName);
    }
}
