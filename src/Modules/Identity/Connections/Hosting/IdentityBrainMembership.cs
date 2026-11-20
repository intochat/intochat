using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework.Modules.Platform.Identity;
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
