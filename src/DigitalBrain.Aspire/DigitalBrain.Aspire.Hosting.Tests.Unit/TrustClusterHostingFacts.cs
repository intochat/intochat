using global::Microsoft.Extensions.Configuration;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Microsoft.Orleans;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class TrustClusterHostingFacts
{
    // R66: the dev trust host advertises loopback; machines dial its link endpoint there (T20-7).
    [Fact]
    public async Task TheTrustHostAdvertisesLoopbackAndMachinesDialItThere()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new()).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);

        var values = await Environment(builder, trust.Resource);

        Assert.Equal(TrustClusterHosting.Loopback, values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.AdvertisedAddressKey)]);
        Assert.Contains(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", "GrainState"), values.Keys);
        // R71: the ports are the trust resource's own endpoints, not literals; the link and relay are allocated UDP
        // endpoints and there is no Orleans gateway (T20-7).
        Assert.IsType<EndpointReferenceExpression>(values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.LinkPortKey)]);
        Assert.IsType<EndpointReferenceExpression>(values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.RelayPortKey)]);
        Assert.IsType<EndpointReferenceExpression>(values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.SiloPortKey)]);
        var endpoints = trust.Resource.Annotations.OfType<EndpointAnnotation>().ToArray();
        Assert.All([TrustClusterHosting.LinkEndpoint, TrustClusterHosting.RelayEndpoint], name => Assert.Contains(endpoints, endpoint =>
            endpoint.Name == name && endpoint.Port is null && endpoint.Protocol == System.Net.Sockets.ProtocolType.Udp));
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Name == "gateway");
        Assert.DoesNotContain(values.Keys, key => key.EndsWith("GatewayPort", StringComparison.Ordinal));
        Assert.Equal("127.0.0.1:{trust.bindings.link.port}", TrustClusterHosting.EndpointOf(trust).ValueExpression);
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
            Assert.Equal("127.0.0.1:{trust.bindings.link.port}", ((ReferenceExpression)values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.EndpointKey)]).ValueExpression);
        });
        Assert.All(new IResource[] { a.Resource, b.Resource }, machine =>
            Assert.Contains(machine.Annotations.OfType<ResourceCommandAnnotation>(), command => command.Name == TrustClusterHosting.ReleasePartCommand));
    }

    // R77: the second machine keeps its own Orleans cluster over the brain reference's, so the cluster comes last; joined to
    // A's cluster it would activate A's grains and open A's parts under its own identity.
    [Fact]
    public async Task TheSecondMachineKeepsItsOwnClusterAfterTheBrainReference()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new()).WithHosting<OrleansModuleHosting, OrleansOptions>(orleans => orleans.WithAzurite());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);
        var pairing = builder.AddParameter("machine-pairing-secret", "s3cret", secret: true);
        var a = builder.AddExecutable("os-a", "unused", ".").WithHttpEndpoint(name: "http").WithReference(brain)
            .AsTrustMachine(trust, pairing, "/machines/os-a", builder.Configuration);
        var b = builder.AddExecutable("os-b", "unused", ".").WithHttpEndpoint(name: "http").WithReference(brain)
            .AsTrustMachine(trust, pairing, "/machines/os-b", builder.Configuration).AsSeparateCluster();

        var (onA, onB) = (await Environment(builder, a.Resource), await Environment(builder, b.Resource));

        Assert.Equal("digitalbrain-os-b", onB["Orleans__ClusterId"]);
        Assert.NotEqual(onA["Orleans__ClusterId"], onB["Orleans__ClusterId"]);
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
        builder.Configuration[TrustClusterHosting.LinkPortKey] = "30200";
        builder.Configuration[TrustClusterHosting.RelayPortKey] = "30201";
        builder.Configuration[TrustClusterHosting.SiloPortKey] = "11300";
        var brain = builder.AddDigitalBrain("memory", options: new());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);

        var endpoints = trust.Resource.Annotations.OfType<EndpointAnnotation>().ToDictionary(endpoint => endpoint.Name, endpoint => endpoint.Port);
        Assert.Equal((30200, 30201, 11300), (endpoints[TrustClusterHosting.LinkEndpoint], endpoints[TrustClusterHosting.RelayEndpoint], endpoints[TrustClusterHosting.SiloEndpoint]));
    }

    // R93: each MCP is a client machine of the deployment: its own directory, the pairing secret and the trust link
    // endpoint; it only dials, so it gets no link port of its own and no part role.
    [Fact]
    public async Task TheMcpResourcesCarryTheirMachineDirectoryPairingAndLinkEndpoint()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);
        var pairing = builder.AddParameter("machine-pairing-secret", "s3cret", secret: true);
        var mcp = builder.AddExecutable("digitalbrain-mcp-a", "unused", ".").AsTrustClientMachine(trust, pairing, "/machines/digitalbrain-mcp-a");

        var values = await Environment(builder, mcp.Resource);

        Assert.Equal("/machines/digitalbrain-mcp-a", values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.MachineDirectoryKey)]);
        Assert.Contains(DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.PairingSecretKey), values.Keys);
        Assert.Equal("127.0.0.1:{trust.bindings.link.port}", ((ReferenceExpression)values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.EndpointKey)]).ValueExpression);
        Assert.DoesNotContain(DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.RoleKey), values.Keys);
        Assert.DoesNotContain(mcp.Resource.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == TrustClusterHosting.LinkEndpoint);
        Assert.Contains(mcp.Resource.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource == trust.Resource);
    }

    // R66 for the link (fix round 1, finding 7): the trust host listens on loopback, its link and relay are plain UDP
    // endpoints (no proxy), and machines dial the loopback address itself, never a name to resolve.
    [Fact]
    public async Task TheTrustLinkIsAdvertisedOnLoopback()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration);
        var pairing = builder.AddParameter("machine-pairing-secret", "s3cret", secret: true);
        var machine = builder.AddExecutable("os-a", "unused", ".").WithHttpEndpoint(name: "http")
            .AsTrustMachine(trust, pairing, "/machines/os-a", builder.Configuration);

        var (onTrust, onMachine) = (await Environment(builder, trust.Resource), await Environment(builder, machine.Resource));

        Assert.Equal(TrustClusterHosting.Loopback, onTrust[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.AdvertisedAddressKey)]);
        Assert.StartsWith(TrustClusterHosting.Loopback + ":", ((ReferenceExpression)onMachine[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.EndpointKey)]).ValueExpression, StringComparison.Ordinal);
        Assert.All(trust.Resource.Annotations.OfType<EndpointAnnotation>().Where(endpoint => endpoint.Name is TrustClusterHosting.LinkEndpoint or TrustClusterHosting.RelayEndpoint),
            endpoint => Assert.False(endpoint.IsProxied));
    }

    // R96: the AppHost owns the deployment's trust identity: it creates (once) or loads the trust host's key in the
    // trust resource's directory and pins every machine and MCP to it, so a Production deployment starts.
    [Fact]
    public async Task EveryMachineCarriesTheTrustPin()
    {
        var identity = Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}", "identity.key");
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration, identity);
        var pairing = builder.AddParameter("machine-pairing-secret", "s3cret", secret: true);
        var machine = builder.AddExecutable("os-a", "unused", ".").WithHttpEndpoint(name: "http")
            .AsTrustMachine(trust, pairing, "/machines/os-a", builder.Configuration);
        var mcp = builder.AddExecutable("digitalbrain-mcp-a", "unused", ".").AsTrustClientMachine(trust, pairing, "/machines/digitalbrain-mcp-a");

        var onTrust = await Environment(builder, trust.Resource);
        var pins = new[] { await Environment(builder, machine.Resource), await Environment(builder, mcp.Resource) }
            .Select(values => values[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.PinKey)]).ToArray();

        Assert.Equal(identity, onTrust[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.IdentityPathKey)]);
        Assert.All(pins, pin => Assert.Equal(pins[0], pin));
        Assert.True(File.Exists(identity));
    }

    [Fact]
    public async Task ThePinEqualsTheTrustHostsKeyId()
    {
        var identity = Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}", "identity.key");
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddDigitalBrain("memory", options: new());
        var trust = builder.AddExecutable("trust", "unused", ".").AsTrustCluster(brain, builder.Configuration, identity);
        var pairing = builder.AddParameter("machine-pairing-secret", "s3cret", secret: true);
        var machine = builder.AddExecutable("os-a", "unused", ".").WithHttpEndpoint(name: "http")
            .AsTrustMachine(trust, pairing, "/machines/os-a", builder.Configuration);

        var pin = (await Environment(builder, machine.Resource))[DigitalBrainConfiguration.EnvironmentName(TrustClusterHosting.PinKey)];

        // The trust host's id, derived independently: the first 16 hex digits of SHA-256 over its key's SubjectPublicKeyInfo.
        using var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportPkcs8PrivateKey(File.ReadAllBytes(identity), out _);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16], pin);
    }

    internal static async Task<Dictionary<string, object>> Environment(IDistributedApplicationBuilder builder, IResource resource)
    {
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, values, TestContext.Current.CancellationToken);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await annotation.Callback(context); }
        return values;
    }
}
