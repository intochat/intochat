using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.Accounts;

public static class LocalClientHosting
{
    public static IServiceCollection AddLocalPlatformClients(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<AuthorityNeurons>();
        services.TryAddSingleton<IIdentityClient, LocalIdentityClient>();
        return services;
    }
}
