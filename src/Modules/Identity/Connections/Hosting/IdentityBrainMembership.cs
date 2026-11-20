using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans;

namespace IntoChat.OS.Connections.Hosting;

// Kernel.Brains() over the identity directory grain the host names (IdentityHostOptions.DirectoryKey).
internal sealed class IdentityBrainMembership(IGrainFactory grains, IOptions<IdentityHostOptions>? identity = null) : IntoChat.OS.Host.Brains.IBrainMembership
{
    private static readonly IdentityHostOptions DefaultIdentity = new();

    public Task<string[]> BrainsOf(string personId, CancellationToken cancellationToken = default)
        => grains.GetGrain<IIdentityDirectory>((identity?.Value ?? DefaultIdentity).DirectoryKey).BrainsOfAsync(personId, cancellationToken);
}

// How a host composes the membership above: the implementation stays the identity module's own.
public static class IdentityBrainMembershipHosting
{
    public static IServiceCollection AddIdentityBrainMembership(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IntoChat.OS.Host.Brains.IBrainMembership, IdentityBrainMembership>();
        return services;
    }
}
