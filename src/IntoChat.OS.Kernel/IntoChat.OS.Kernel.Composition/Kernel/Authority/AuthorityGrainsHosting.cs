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
        services.TryAddSingleton<IntoChat.OS.Runtime.IStillGrains, StillGrains>();
        return services;
    }

    // 06-08: a cluster client host (the MCP) reaches plain neurons (apps among them) through the runtime's host grains, as the
    // silo does; the silo's call filters still judge each call by the stamped caller.
    public static IServiceCollection AddPlainNeurons(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IntoChat.OS.Runtime.IPlainNeurons>(provider => new PlainNeurons(provider.GetRequiredService<IGrainFactory>(), services: provider));
        services.TryAddSingleton<IntoChat.OS.Runtime.IPlainSignals, PlainSignals>();
        return services;
    }
}
