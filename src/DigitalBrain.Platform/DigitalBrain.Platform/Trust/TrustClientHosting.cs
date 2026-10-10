using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Marketplace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Trust;

public static class TrustClientHosting
{
    // The trust clients, the same for a machine silo and an Orleans-client host such as the MCP (R70): through the
    // private gateway client when DigitalBrain:Platform:Trust:Gateway is set, else through the host's own grain
    // factory. Only a host that signs (a machine) supplies an identity; the others read.
    public static IServiceCollection AddTrustClients(this IServiceCollection services, bool signs)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<TrustOptions>().BindConfiguration(TrustOptions.Key)
            .Validate(trust => { trust.Validate(); return true; }).ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TrustGatewayClient>();
        services.TryAddSingleton(provider =>
        {
            Func<IGrainFactory> grains = provider.GetRequiredService<IOptions<TrustOptions>>().Value.Gateway is null
                ? provider.GetRequiredService<IGrainFactory>
                : () => provider.GetRequiredService<TrustGatewayClient>().Grains;
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

    // An Orleans-client host has no silo lifecycle: its gateway client starts as a hosted service.
    public static IServiceCollection AddTrustClientsForClientHost(this IServiceCollection services)
    {
        services.AddTrustClients(signs: false);
        services.AddHostedService<TrustGatewayHostedService>();
        return services;
    }

    private sealed class TrustGatewayHostedService(TrustGatewayClient client, IOptions<TrustOptions> options) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => options.Value.Gateway is null ? Task.CompletedTask : client.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => client.StopAsync(cancellationToken);
    }
}
