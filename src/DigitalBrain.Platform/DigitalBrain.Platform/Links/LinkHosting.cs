using System.Runtime.Versioning;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Marketplace;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Links;

public static class LinkHosting
{
    // A machine of a separate trust cluster (DigitalBrain:Platform:Trust:Endpoint set): its links to the trust host
    // and to other machines, and the five trust clients over the trust link (T20-7). Registered ahead of
    // AddTrustClients, whose grain clients are then never used on this host.
    public static ISiloBuilder AddMachineLinks(this ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Links(silo.Services);
            return silo;
        }
        throw new PlatformNotSupportedException("Machine links need QUIC (Windows, Linux or macOS).");
    }

    [SupportedOSPlatform("windows"), SupportedOSPlatform("linux"), SupportedOSPlatform("macos")]
    private static void Links(IServiceCollection services)
    {
        if (services.Any(service => service.ServiceType == typeof(QuicMachineTransport))) { return; }
        services.AddOptions<LinkOptions>().BindConfiguration(LinkOptions.Key)
            .Validate(links => { links.Validate(); return true; }).ValidateOnStart();
        services.AddOptions<TrustOptions>().Validate<IHostEnvironment>((trust, environment) =>
        {
            TrustPin.RequireInProduction(environment.EnvironmentName, trust);
            return true;
        }).ValidateOnStart();
        services.TryAddSingleton<TrustPin>();
        services.AddSingleton(provider =>
        {
            var transport = ActivatorUtilities.CreateInstance<QuicMachineTransport>(provider);
            transport.RendezvousHandler = new RendezvousHandler(transport, provider.GetRequiredService<ILogger<RendezvousHandler>>());
            return transport;
        });
        services.AddSingleton<IMachineTransport>(provider => provider.GetRequiredService<QuicMachineTransport>());
        services.AddSingleton(provider => new TrustSigner(provider.GetRequiredService<Sequencer.MachineIdentity>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<MachineCounter>()));
        services.AddSingleton(provider => new LinkTrustClients(provider.GetRequiredService<QuicMachineTransport>(),
            provider.GetRequiredService<Orleans.Serialization.Serializer>(), provider.GetRequiredService<TrustSigner>())
        { PairingSecretHash = MachinePairing.HashOf(provider.GetRequiredService<IOptions<Parts.PartsOptions>>().Value) });
        services.AddSingleton<ISequencerClient>(provider => provider.GetRequiredService<LinkTrustClients>());
        services.AddSingleton<INodeDirectoryClient>(provider => provider.GetRequiredService<LinkTrustClients>());
        services.AddSingleton<IKeyringClient>(provider => provider.GetRequiredService<LinkTrustClients>());
        services.AddSingleton<IMarketplaceClient>(provider => provider.GetRequiredService<LinkTrustClients>());
        services.AddSingleton<IIdentityClient>(provider => provider.GetRequiredService<LinkTrustClients>());
    }
}
