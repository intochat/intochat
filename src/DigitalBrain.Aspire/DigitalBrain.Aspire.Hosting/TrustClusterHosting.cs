using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using System.Net.Http.Headers;
using System.Text;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Aspire.Hosting;

// The trust cluster beside the OS machines (Task 18): its own host on the orleans integration's tables and blobs,
// reached by each machine through a private gateway client.
public static class TrustClusterHosting
{
    public const string GatewayPortKey = "DigitalBrain:Platform:Trust:GatewayPort";
    public const string SiloPortKey = "DigitalBrain:Platform:Trust:SiloPort";
    public const string SeedNodesKey = "DigitalBrain:Platform:Mesh:SeedNodes";
    public const int DefaultSeedNodes = 24;
    public const string GatewayEndpoint = "gateway";
    public const string SiloEndpoint = "silo";
    public const string AdvertisedAddressKey = "DigitalBrain:Platform:Trust:AdvertisedAddress";
    public const string GatewayKey = "DigitalBrain:Platform:Trust:Gateway";
    public const string Loopback = "127.0.0.1";

    // R66: on one PC the trust silo advertises and listens on loopback, so a machine's localhost gateway reaches it.
    // R71: its silo and gateway ports are Aspire endpoints, allocated per run unless the AppHost configuration fixes
    // them (DigitalBrain:Platform:Trust:GatewayPort / SiloPort), so two AppHosts can run side by side.
    public static IResourceBuilder<T> AsTrustCluster<T>(this IResourceBuilder<T> trust, DigitalBrainBuilder brain, IConfiguration configuration)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(configuration);
        trust.WithEndpoint(name: GatewayEndpoint, port: configuration.GetValue<int?>(GatewayPortKey), scheme: "tcp", isProxied: false)
            .WithEndpoint(name: SiloEndpoint, port: configuration.GetValue<int?>(SiloPortKey), scheme: "tcp", isProxied: false);
        trust.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(GatewayPortKey), trust.GetEndpoint(GatewayEndpoint).Property(EndpointProperty.Port))
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(SiloPortKey), trust.GetEndpoint(SiloEndpoint).Property(EndpointProperty.Port))
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(AdvertisedAddressKey), Loopback)
            // The seed nodes are registered by the trust host's SeedNodeRegistration (Q25); the machines only place on them.
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(SeedNodesKey),
                configuration.GetValue(SeedNodesKey, DefaultSeedNodes).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (brain.Clustering is { } tables && brain.GrainState is { } blobs)
        {
            trust.WithEnvironment(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", "Clustering"), tables.Resource.ConnectionStringExpression)
                .WithEnvironment(DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", "GrainState"), blobs.Resource.ConnectionStringExpression)
                .WaitFor(tables).WaitFor(blobs);
        }
        return trust;
    }

    // The address machines dial: localhost on the trust silo's allocated gateway port.
    public static ReferenceExpression GatewayOf<TTrust>(IResourceBuilder<TTrust> trust) where TTrust : IResourceWithEndpoints
        => ReferenceExpression.Create($"localhost:{trust.GetEndpoint(GatewayEndpoint).Property(EndpointProperty.Port)}");

    public const string PairingSecretKey = "DigitalBrain:Platform:Parts:PairingSecret";
    public const string RoleKey = "DigitalBrain:Platform:Parts:Role";
    public const string MachineDirectoryKey = "DigitalBrain:Platform:Parts:MachineDirectory";
    public const string CoreHolderKey = "DigitalBrain:Platform:Parts:CoreHolder";
    public const string ReleasePartCommand = "release-part";
    private static readonly string ModuleGrainStateContainerKey = DigitalBrainConfiguration.ModuleOptionsSection("orleans") + ":GrainStateContainer";

    // An own machine of the trust cluster: the private gateway, the deployment's pairing secret (R52), its own
    // directory and its own grain-state container (R61: machines keep local platform storage, never shared), and
    // the release-part command (R40, R64).
    public static IResourceBuilder<T> AsTrustMachine<T, TTrust>(this IResourceBuilder<T> machine, IResourceBuilder<TTrust> trust,
        IResourceBuilder<ParameterResource> pairingSecret, string machineDirectory, IConfiguration configuration)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport, IResourceWithEndpoints
        where TTrust : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(machine);
        var container = ContainerOf(machine.Resource.Name);
        return machine
            .AsTrustClient(trust)
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(PairingSecretKey), pairingSecret)
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(RoleKey), "Own")
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(MachineDirectoryKey), machineDirectory)
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(DigitalBrain.Framework.DigitalBrainNames.GrainStateContainerKey), container)
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(ModuleGrainStateContainerKey), container)
            .WithPartRelease(configuration);
    }

    // R76: the primary machine the fronts and MCPs route to holds the owner's core; no other machine races for it at boot.
    public static IResourceBuilder<T> AsCoreHolder<T>(this IResourceBuilder<T> machine) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(CoreHolderKey), "true");
    }

    public const string OrleansSiloEndpoint = "orleans-silo";
    public const string OrleansGatewayEndpoint = "orleans-gateway";

    // A further machine on one PC: its own Orleans cluster, with silo and gateway ports allocated per run like the
    // trust's (R71), so two AppHosts run side by side.
    public static IResourceBuilder<T> AsSeparateCluster<T>(this IResourceBuilder<T> machine)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(machine);
        machine.WithEndpoint(name: OrleansSiloEndpoint, scheme: "tcp", isProxied: false)
            .WithEndpoint(name: OrleansGatewayEndpoint, scheme: "tcp", isProxied: false);
        return machine
            .WithEnvironment("Orleans__ClusterId", ClusterOf(machine.Resource.Name))
            .WithEnvironment("Orleans__Endpoints__SiloPort", machine.GetEndpoint(OrleansSiloEndpoint).Property(EndpointProperty.Port))
            .WithEnvironment("Orleans__Endpoints__GatewayPort", machine.GetEndpoint(OrleansGatewayEndpoint).Property(EndpointProperty.Port));
    }

    // An Orleans-client host of the deployment (the MCP): it reads trust state through the same private gateway the
    // machines dial (R70), so it starts once the trust silo is up.
    public static IResourceBuilder<T> AsTrustClient<T, TTrust>(this IResourceBuilder<T> client, IResourceBuilder<TTrust> trust)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
        where TTrust : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(trust);
        return client
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(GatewayKey), GatewayOf(trust))
            .WaitFor(client.ApplicationBuilder.CreateResourceBuilder<IResource>(trust.Resource));
    }

    public static string ContainerOf(string machine) => "grainstate-" + machine.ToLowerInvariant();

    public static string ClusterOf(string machine) => "digitalbrain-" + machine.ToLowerInvariant();

    public static IResourceBuilder<T> WithPartRelease<T>(this IResourceBuilder<T> machine, IConfiguration configuration)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(configuration);
        return machine.WithHttpCommand("/machine/parts/release", "Release part", commandName: ReleasePartCommand, endpointName: "http", commandOptions: new HttpCommandOptions
        {
            Description = "Release one part so another machine of this owner can take it.",
            Arguments = [new InteractionInput { Name = "part", Label = "Part", InputType = InputType.Text, Required = true }],
            PrepareRequest = request =>
            {
                var part = request.Arguments.GetString("part") ?? "";
                request.Request.RequestUri = new Uri(request.Request.RequestUri!, "/machine/parts/" + Uri.EscapeDataString(part) + "/release");
                request.Request.Headers.Authorization = Credential(configuration);
                return Task.CompletedTask;
            },
        });
    }

    // R64: under Secured the command speaks as the host's bootstrap user; Open sends no credential.
    public static AuthenticationHeaderValue? Credential(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.Equals(configuration[AuthOptions.PostureKey], nameof(IdentityPosture.Secured), StringComparison.OrdinalIgnoreCase)) { return null; }
        var username = configuration[AuthOptions.Key(nameof(AuthOptions.Username))];
        var password = configuration[AuthOptions.Key(nameof(AuthOptions.Password))];
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) { return null; }
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
    }
}
