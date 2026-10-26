using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-06c/06-08): a host that composes Authority's readers outside the silo (the MCP, an Orleans client) reaches
// Authority's raw grains through the same allow-listed accessor the silo registers.
public static class AuthorityGrainsHosting
{
    public static IServiceCollection AddAuthorityGrains(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IntoChat.OS.Authority.IAuthorityGrains, AuthorityGrains>();
        return services;
    }
}
