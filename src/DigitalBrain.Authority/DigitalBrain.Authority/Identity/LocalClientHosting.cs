using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Marketplace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Authority.Identity;

public static class LocalClientHosting
{
    public static IServiceCollection AddLocalPlatformClients(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<AuthorityNeurons>();
        services.TryAddSingleton<IIdentityClient, LocalIdentityClient>();
        services.TryAddSingleton<IMarketplaceClient, Marketplace.LocalMarketplaceClient>();
        return services;
    }
}
