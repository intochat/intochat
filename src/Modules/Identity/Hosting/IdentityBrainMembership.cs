using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Identity.Hosting;

// Kernel.Brains() over the unified identity directory client.
internal sealed class IdentityBrainMembership(IIdentityClient identity) : IntoChat.OS.Host.Brains.IBrainMembership
{
    public async Task<string[]> BrainsOf(string personId, CancellationToken cancellationToken = default)
        => [.. (await identity.BrainsOf(personId).WaitAsync(cancellationToken).ConfigureAwait(false)).Select(brain => brain.Id)];
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
