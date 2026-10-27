using DigitalBrain;
using DigitalBrain.Capabilities;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.Cloud.Circle.Server;

// R4-01, CLOUD-02/03: the server side of a circle (enrollment, the circle credential, machine authentication) as its
// own module, so the later Azure Blob and Organisations dependencies never load on a personal computer. It is
// composed only when Topology=cloud; the kernel adds it through CloudModules.For in 10-16. Later plans add their
// routes in their own partial files (journal 10-10, store 10-11, files 10-14, approval).
[Module(Id)]
public sealed partial class CircleServerModule : Module
{
    public const string Id = "circle-server";
    public const string OptionsSection = "IntoChatOS:Modules:" + Id + ":Options";
    // The os cluster grain store; module state uses the silo serializer until secrets@1 holds credentials.
    public const string GrainStore = "cluster";
    // A keyed TimeProvider under this key replaces the system clock for credentials and proofs (tests).
    public const string ClockKey = "circle-server";
    public const string CloudTopology = "cloud";
    internal const string TopologyKey = "IntoChatOS:Topology";

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [CapabilityId.Parse("identity@1")],
        Needs: [Need.GrainStore(GrainStore), Need.Http],
        Settings: [Setting.Of<CircleServerOptions>(OptionsSection)],
        Secrets: [Secret.Named("circle-deployment-key")]);

    public static bool LoadsUnder(string? topology) => string.Equals(topology?.Trim(), CloudTopology, StringComparison.OrdinalIgnoreCase);

    // The module for this host's configuration, or null for any topology but cloud.
    public static CircleServerModule? For(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return LoadsUnder(configuration[TopologyKey]) ? new CircleServerModule() : null;
    }

    // Silo services. The host also supplies ICircleInviteRedeemer (the os circles module, 10-03).
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<CircleServerOptions>().BindConfiguration(OptionsSection);
        services.TryAddSingleton<EnrollmentRateLimits>();
        services.TryAddSingleton<INonceCache>(provider => new NonceCache(Clock(provider)));
        ConfigureAuthentication(services);
    }

    public void Configure(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        MapEnrollment(routes);
        MapMembership(routes);
        MapJournal(routes);
        MapStore(routes);
        MapFiles(routes);
        MapApproval(routes);
    }

    // MachineAuthentication.cs: the IntoChatMachine scheme, its policies, leave and status.
    partial void ConfigureAuthentication(IServiceCollection services);

    partial void MapMembership(IEndpointRouteBuilder routes);

    partial void MapJournal(IEndpointRouteBuilder routes);

    partial void MapStore(IEndpointRouteBuilder routes);

    partial void MapFiles(IEndpointRouteBuilder routes);

    partial void MapApproval(IEndpointRouteBuilder routes);

    internal static TimeProvider Clock(IServiceProvider services) => services.GetKeyedService<TimeProvider>(ClockKey) ?? TimeProvider.System;
}
