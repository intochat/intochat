using global::Microsoft.Extensions.Configuration;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Microsoft.Orleans;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class TrustClusterHostingFacts
{
    // R66: the dev trust silo advertises loopback, and the machines' gateway is localhost on the same port.
    [Fact]
    public async Task TheTrustSiloAdvertisesLoopbackAndMachinesReachItOnLocalhost()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new()).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);

        var values = await Environment(builder, trust.Resource);

        Assert.Equal(TrustClusterHosting.Loopback, values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.AdvertisedAddressKey)]);
        Assert.Contains(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", "GrainState"), values.Keys);
        // R71: the ports are the trust resource's own endpoints, not literals, and machines dial that gateway.
        Assert.IsType<EndpointReferenceExpression>(values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.GatewayPortKey)]);
        Assert.IsType<EndpointReferenceExpression>(values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.SiloPortKey)]);
        Assert.Contains(trust.Resource.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == TrustClusterHosting.GatewayEndpoint && endpoint.Port is null);
        Assert.Equal("localhost:{trust.bindings.gateway.port}", TrustClusterHosting.GatewayOf(trust).ValueExpression);
    }

    [Fact]
    public async Task TheTrustResourceStoresOnPlatformStorageAndComposesNoModule()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new()).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);

        var values = await Environment(builder, trust.Resource);

        Assert.Contains(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", "GrainState"), values.Keys);
        Assert.DoesNotContain(values.Keys, key => key.StartsWith("DigitalBrain__Modules__", StringComparison.Ordinal));
        Assert.DoesNotContain(values.Keys, key => key.StartsWith("DigitalBrain__Integrations__", StringComparison.Ordinal)
            && !key.StartsWith("DigitalBrain__Integrations__orleans__", StringComparison.Ordinal));
    }

    // R61: each machine has its own directory and grain-state container; the second also its own Orleans cluster.
    [Fact]
    public async Task EachMachineHasItsOwnDirectoryContainerAndClusterAndThePairingSecret()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new()).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);
        var pairing = builder.AddParameter("machine-pairing-secret", "s3cret", secret: true);
        var a = builder.AddExecutable("os-a", "unused", ".").WithHttpEndpoint(name: "http")
            .AsTrustMachine(trust, pairing, "/machines/os-a", builder.Configuration);
        var b = builder.AddExecutable("os-b", "unused", ".").WithHttpEndpoint(name: "http")
            .AsTrustMachine(trust, pairing, "/machines/os-b", builder.Configuration).AsSeparateCluster();

        var (onA, onB) = (await Environment(builder, a.Resource), await Environment(builder, b.Resource));

        var directory = DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.MachineDirectoryKey);
        var container = DigitalBrainConfiguration.EnvironmentName(DigitalBrain.Framework.DigitalBrainNames.GrainStateContainerKey);
        Assert.Equal(("/machines/os-a", "/machines/os-b"), (onA[directory], onB[directory]));
        Assert.NotEqual(onA[container], onB[container]);
        Assert.Equal("digitalbrain-os-b", onB["Orleans__ClusterId"]);
        Assert.IsType<EndpointReferenceExpression>(onB["Orleans__Endpoints__SiloPort"]);
        Assert.IsType<EndpointReferenceExpression>(onB["Orleans__Endpoints__GatewayPort"]);
        Assert.Contains(b.Resource.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == TrustClusterHosting.OrleansGatewayEndpoint && endpoint.Port is null);
        Assert.All([onA, onB], values =>
        {
            Assert.Contains(DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.PairingSecretKey), values.Keys);
            Assert.Equal("localhost:{trust.bindings.gateway.port}", ((ReferenceExpression)values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.GatewayKey)]).ValueExpression);
        });
        Assert.All(new IResource[] { a.Resource, b.Resource }, machine =>
            Assert.Contains(machine.Annotations.OfType<ResourceCommandAnnotation>(), command => command.Name == TrustClusterHosting.ReleasePartCommand));
    }

    // R76: only the primary machine holds the owner's core; the second machine carries no core-holder key.
    [Fact]
    public async Task OnlyMachineAIsTheCoreHolder()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new()).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);
        var pairing = builder.AddParameter("machine-pairing-secret", "s3cret", secret: true);
        var a = builder.AddExecutable("os-a", "unused", ".").WithHttpEndpoint(name: "http")
            .AsTrustMachine(trust, pairing, "/machines/os-a", builder.Configuration).AsCoreHolder();
        var b = builder.AddExecutable("os-b", "unused", ".").WithHttpEndpoint(name: "http")
            .AsTrustMachine(trust, pairing, "/machines/os-b", builder.Configuration).AsSeparateCluster();

        var key = DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.CoreHolderKey);
        Assert.Equal("true", (await Environment(builder, a.Resource))[key]);
        Assert.DoesNotContain(key, (await Environment(builder, b.Resource)).Keys);
    }

    // Round 3 (d): the seed nodes live with the trust host that registers them, never on a machine.
    [Fact]
    public async Task TheTrustResourceRegistersTheSeedNodes()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new()).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);
        var pairing = builder.AddParameter("machine-pairing-secret", "s3cret", secret: true);
        var machine = builder.AddExecutable("os-a", "unused", ".").WithHttpEndpoint(name: "http")
            .AsTrustMachine(trust, pairing, "/machines/os-a", builder.Configuration);

        var seeds = DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.SeedNodesKey);
        Assert.Equal("24", (await Environment(builder, trust.Resource))[seeds]);
        Assert.DoesNotContain(seeds, (await Environment(builder, machine.Resource)).Keys);
    }

    // R70: the MCP is an Orleans client of the deployment; it reaches the trust cluster through the allocated gateway.
    [Fact]
    public async Task TheMcpCarriesTheTrustGateway()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new()).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);
        var mcp = builder.AddExecutable("digitalbrain-mcp-a", "unused", ".").WithReference(brain.AsClient()).AsTrustClient(trust);

        var values = await Environment(builder, mcp.Resource);

        Assert.Equal("localhost:{trust.bindings.gateway.port}",
            ((ReferenceExpression)values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.GatewayKey)]).ValueExpression);
        Assert.Contains(mcp.Resource.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource == trust.Resource);
    }

    // R64: Secured sends the host's bootstrap Basic credential; Open sends none.
    [Fact]
    public void TheReleaseCommandCarriesTheBootstrapCredentialOnlyWhenSecured()
    {
        var secured = new global::Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DigitalBrain.Platform.Identity.Configuration.AuthOptions.PostureKey] = "Secured",
            [DigitalBrain.Platform.Identity.Configuration.AuthOptions.Key("Username")] = "operator",
            [DigitalBrain.Platform.Identity.Configuration.AuthOptions.Key("Password")] = "configured-bootstrap-password",
        }).Build();
        var open = new global::Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DigitalBrain.Platform.Identity.Configuration.AuthOptions.PostureKey] = "Open",
        }).Build();

        var credential = TrustClusterHosting.Credential(secured);
        Assert.Equal("Basic", credential?.Scheme);
        Assert.Equal("operator:configured-bootstrap-password", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(credential!.Parameter!)));
        Assert.Null(TrustClusterHosting.Credential(open));
    }

    // R71: an AppHost that fixes the trust ports gets exactly those.
    [Fact]
    public void ConfiguredTrustPortsAreUsedAsGiven()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[TrustClusterHosting.GatewayPortKey] = "30200";
        builder.Configuration[TrustClusterHosting.SiloPortKey] = "11300";
        var brain = builder.AddDigitalBrain("memory", options: new());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);

        var endpoints = trust.Resource.Annotations.OfType<EndpointAnnotation>().ToDictionary(endpoint => endpoint.Name, endpoint => endpoint.Port);
        Assert.Equal((30200, 11300), (endpoints[TrustClusterHosting.GatewayEndpoint], endpoints[TrustClusterHosting.SiloEndpoint]));
    }

    internal static async Task<Dictionary<string, object>> Environment(IDistributedApplicationBuilder builder, IResource resource)
    {
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, values, TestContext.Current.CancellationToken);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await annotation.Callback(context); }
        return values;
    }
}
