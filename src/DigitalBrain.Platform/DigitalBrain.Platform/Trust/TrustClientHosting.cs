using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Marketplace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Trust;

public static class TrustClientHosting
{
    // The trust clients over the grains of this host's own silo (Stage A, tests). A machine of a separate trust
    // cluster has registered its link clients first (AddMachineLinks), so these TryAdds keep those. Only a host
    // that signs (a machine) supplies an identity; the others read.
    public static IServiceCollection AddTrustClients(this IServiceCollection services, bool signs)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<TrustOptions>().BindConfiguration(TrustOptions.Key)
            .Validate(trust => { trust.Validate(); return true; }).ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider =>
        {
            Func<IGrainFactory> grains = provider.GetRequiredService<IGrainFactory>;
            if (!signs) { return GrainTrustClients.Reading(grains, provider.GetRequiredService<TimeProvider>()); }
            return new GrainTrustClients(grains, provider.GetRequiredService<Sequencer.MachineIdentity>(), provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<MachineCounter>())
            { PairingSecretHash = MachinePairing.HashOf(provider.GetRequiredService<IOptions<Parts.PartsOptions>>().Value) };
        });
        services.TryAddSingleton<ISequencerClient>(provider => provider.GetRequiredService<GrainTrustClients>());
        services.TryAddSingleton<INodeDirectoryClient>(provider => provider.GetRequiredService<GrainTrustClients>());
        services.TryAddSingleton<IKeyringClient>(provider => provider.GetRequiredService<GrainTrustClients>());
        services.TryAddSingleton<IMarketplaceClient>(provider => provider.GetRequiredService<GrainTrustClients>());
        services.TryAddSingleton<IIdentityClient>(provider => provider.GetRequiredService<GrainTrustClients>());
        return services;
    }

    // An Orleans-client host (the MCP, R70) reads trust state through the clients its process composes.
    public static IServiceCollection AddTrustClientsForClientHost(this IServiceCollection services) => services.AddTrustClients(signs: false);
}
