using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Connections.Platform.Marketplace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Authority.Identity;

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
