using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using System.Net.Http.Headers;
using System.Text;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Aspire.Hosting;

// The trust cluster beside the OS machines (Task 18): its own host on the orleans integration's tables and blobs,
// reached by each machine over its QUIC link to the trust host's link endpoint, with the relay beside it (T20-7).
public static class TrustClusterHosting
{
    public const string LinkPortKey = "DigitalBrain:Platform:Trust:LinkPort";
    public const string RelayPortKey = "DigitalBrain:Platform:Trust:RelayPort";
    public const string SiloPortKey = "DigitalBrain:Platform:Trust:SiloPort";
    public const string SeedNodesKey = "DigitalBrain:Platform:Mesh:SeedNodes";
    public const int DefaultSeedNodes = 24;
    public const string LinkEndpoint = "link";
    public const string RelayEndpoint = "relay";
    public const string SiloEndpoint = "silo";
    public const string AdvertisedAddressKey = "DigitalBrain:Platform:Trust:AdvertisedAddress";
    public const string EndpointKey = "DigitalBrain:Platform:Trust:Endpoint";
    public const string PinKey = DigitalBrain.Framework.DigitalBrainNames.TrustPinConfigurationKey;
    public const string IdentityPathKey = "DigitalBrain:Platform:Trust:IdentityPath";
    public const string Loopback = "127.0.0.1";

    // R66: on one PC the trust host advertises and listens on loopback, so a machine's localhost endpoint reaches it.
    // R71: its silo port and its UDP link and relay ports are Aspire endpoints, allocated per run unless the AppHost
    // configuration fixes them (DigitalBrain:Platform:Trust:LinkPort / RelayPort / SiloPort), so two AppHosts can run
    // side by side. It opens no Orleans gateway (T20-7).
    // R96: with an identity path the AppHost owns the deployment's trust identity: it creates the trust host's key there
    // once (or loads it), points the trust host at the same file, and pins every machine and MCP to its id.
    public static IResourceBuilder<T> AsTrustCluster<T>(this IResourceBuilder<T> trust, DigitalBrainBuilder brain, IConfiguration configuration,
        string? identityPath = null)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(configuration);
        if (identityPath is { Length: > 0 })
        {
            trust.WithAnnotation(new TrustPinAnnotation(DigitalBrain.Framework.Parts.MachineKeyFile.IdOfFile(identityPath)))
                .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IdentityPathKey), identityPath);
        }
        trust.WithEndpoint(name: LinkEndpoint, port: configuration.GetValue<int?>(LinkPortKey), scheme: "udp", isProxied: false, protocol: System.Net.Sockets.ProtocolType.Udp)
            .WithEndpoint(name: RelayEndpoint, port: configuration.GetValue<int?>(RelayPortKey), scheme: "udp", isProxied: false, protocol: System.Net.Sockets.ProtocolType.Udp)
            .WithEndpoint(name: SiloEndpoint, port: configuration.GetValue<int?>(SiloPortKey), scheme: "tcp", isProxied: false);
        trust.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(LinkPortKey), trust.GetEndpoint(LinkEndpoint).Property(EndpointProperty.Port))
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(RelayPortKey), trust.GetEndpoint(RelayEndpoint).Property(EndpointProperty.Port))
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

    // The address machines dial: the loopback address the trust host listens on (R66), with its allocated link port;
    // an address, not a name, so no machine resolves it to another interface.
    public static ReferenceExpression EndpointOf<TTrust>(IResourceBuilder<TTrust> trust) where TTrust : IResourceWithEndpoints
        => ReferenceExpression.Create($"{Loopback}:{trust.GetEndpoint(LinkEndpoint).Property(EndpointProperty.Port)}");

    public const string PairingSecretKey = "DigitalBrain:Platform:Parts:PairingSecret";
    public const string RoleKey = "DigitalBrain:Platform:Parts:Role";
    public const string MachineDirectoryKey = "DigitalBrain:Platform:Parts:MachineDirectory";
    public const string CoreHolderKey = "DigitalBrain:Platform:Parts:CoreHolder";
    public const string ReleasePartCommand = "release-part";
    private static readonly string ModuleGrainStateContainerKey = DigitalBrainConfiguration.ModuleOptionsSection("orleans") + ":GrainStateContainer";

    // An own machine of the trust cluster: the trust link endpoint, the deployment's pairing secret (R52), its own
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
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(EndpointKey), EndpointOf(trust))
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
    // trust's (R71), so two AppHosts run side by side. Call it after the brain reference: the reference sets the
    // deployment's cluster id and ports too, and the later environment callback wins (R77).
    public static IResourceBuilder<T> AsSeparateCluster<T>(this IResourceBuilder<T> machine)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine.WithAllocatedOrleansPorts().WithEnvironment("Orleans__ClusterId", ClusterOf(machine.Resource.Name));
    }

    // Task 22 / R71: the machine stays in the cluster its brain reference names, on silo and gateway ports allocated
    // per run instead of Orleans' defaults. Call it after the brain reference, like AsSeparateCluster.
    public static IResourceBuilder<T> WithAllocatedOrleansPorts<T>(this IResourceBuilder<T> machine)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(machine);
        machine.WithEndpoint(name: OrleansSiloEndpoint, scheme: "tcp", isProxied: false)
            .WithEndpoint(name: OrleansGatewayEndpoint, scheme: "tcp", isProxied: false);
        return machine
            .WithEnvironment("Orleans__Endpoints__SiloPort", machine.GetEndpoint(OrleansSiloEndpoint).Property(EndpointProperty.Port))
            .WithEnvironment("Orleans__Endpoints__GatewayPort", machine.GetEndpoint(OrleansGatewayEndpoint).Property(EndpointProperty.Port));
    }

    // Task 22 / R82: a cloud machine of the trust cluster: short leases, no journal, refused the core (the only private
    // part in this release); calls for the core are forwarded to its holder.
    public static IResourceBuilder<T> AsCloudMachine<T>(this IResourceBuilder<T> machine) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(machine);
        return machine.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(RoleKey), "Cloud");
    }

    // A host of the deployment that starts once the trust host is up.
    public static IResourceBuilder<T> AsTrustClient<T, TTrust>(this IResourceBuilder<T> client, IResourceBuilder<TTrust> trust)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
        where TTrust : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(trust);
        if (trust.Resource.Annotations.OfType<TrustPinAnnotation>().FirstOrDefault() is { } pin)
        { client.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(PinKey), pin.TrustId); }
        return client.WaitFor(client.ApplicationBuilder.CreateResourceBuilder<IResource>(trust.Resource));
    }

    // R93: an Orleans-client host (the MCP) is a machine that holds no part: its own directory, the deployment's
    // pairing secret and the trust link endpoint. It only dials the trust link, so it needs no link port of its own.
    public static IResourceBuilder<T> AsTrustClientMachine<T, TTrust>(this IResourceBuilder<T> client, IResourceBuilder<TTrust> trust,
        IResourceBuilder<ParameterResource> pairingSecret, string machineDirectory)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
        where TTrust : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(client);
        return client
            .AsTrustClient(trust)
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(EndpointKey), EndpointOf(trust))
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(PairingSecretKey), pairingSecret)
            .WithEnvironment(DigitalBrainConfiguration.EnvironmentName(MachineDirectoryKey), machineDirectory);
    }

    // The trust host's id, carried on its resource for the machines that pin it (R96).
    public sealed record TrustPinAnnotation(string TrustId) : IResourceAnnotation;

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
