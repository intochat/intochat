using System.Net;
using System.Runtime.Versioning;
using DigitalBrain.Framework.Links;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Platform;

public static class TrustHosting
{
    // The trust cluster: sequencer, node directory and machine registry, keyrings, identity, ledger and the
    // Marketplace catalog, all on `platform` storage. It composes no neuron, no module and no custody (R30, R33):
    // it never sees a secret, a credential or an unwrapped key. A separate trust host adds its link endpoint and relay
    // (AddTrustLinks); a machine composing these grains in its own silo (Stage A) has none.
    public static ISiloBuilder AddTrustCluster(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        var services = silo.Services;
        if (services.Any(service => service.ServiceType == typeof(TrustRegistration))) { return silo; }
        services.AddSingleton<TrustRegistration>();
        services.TryAddSingleton(TimeProvider.System);
        // The node directory's clock (heartbeats, T23-5), apart from the silo's so a fact can move it alone.
        services.TryAddKeyedSingleton(MeshOptions.Key, (provider, _) => provider.GetRequiredService<TimeProvider>());
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName);
        services.AddOptions<MeshOptions>().BindConfiguration(MeshOptions.Key)
            .Validate(mesh => { mesh.Validate(); return true; }).ValidateOnStart();
        services.AddOptions<StorageCreditOptions>().BindConfiguration(StorageCreditOptions.Key)
            .Validate(credits => { credits.Validate(); return true; }).ValidateOnStart();
        silo.AddStartupTask<SeedNodeRegistration>();
        return silo;
    }

    // A separate trust host refuses unsigned trust writes from outside its cluster (R63). A machine composing the trust
    // grains in its own silo (Stage A) has no such boundary to keep.
    public static ISiloBuilder AddTrustBoundary(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.AddIncomingGrainCallFilter<Trust.TrustBoundaryFilter>();
        return silo;
    }

    // The trust host's link endpoint and relay (T20-5, T20-7): machines reach the trust grains only through them; the
    // host opens no Orleans gateway.
    public static ISiloBuilder AddTrustLinks(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Links(silo.Services);
            return silo;
        }
        throw new PlatformNotSupportedException("The trust link endpoint needs QUIC (Windows, Linux or macOS).");
    }

    [SupportedOSPlatform("windows"), SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
    private static void Links(IServiceCollection services)
    {
        services.AddOptions<Trust.TrustOptions>().BindConfiguration(Trust.TrustOptions.Key)
            .Validate(trust => { trust.Validate(); return true; }).ValidateOnStart();
        services.AddOptions<LinkOptions>().BindConfiguration(LinkOptions.Key)
            .Validate(links => { links.Validate(); return true; }).ValidateOnStart();
        services.TryAddSingleton<TrustLinkEndpoint>();
        services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(provider => provider.GetRequiredService<TrustLinkEndpoint>());
    }

    // Where a started trust host listens: its link endpoint, its relay, and the id machines pin (T20-3).
    public static (IPEndPoint Link, IPEndPoint Relay, string TrustId) TrustLinks(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var endpoint = services.GetRequiredService<TrustLinkEndpoint>();
            return (endpoint.LinkEndPoint, endpoint.RelayEndPoint, endpoint.TrustId);
        }
        throw new PlatformNotSupportedException("The trust link endpoint needs QUIC (Windows, Linux or macOS).");
    }

    private sealed class TrustRegistration;
}
