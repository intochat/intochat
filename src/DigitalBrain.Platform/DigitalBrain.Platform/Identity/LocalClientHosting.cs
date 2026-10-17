using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Marketplace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Platform.Identity;

public static class LocalClientHosting
{
    public static IServiceCollection AddLocalPlatformClients(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IIdentityClient, LocalIdentityClient>();
        services.TryAddSingleton<IMarketplaceClient, Marketplace.LocalMarketplaceClient>();
        return services;
    }
}
