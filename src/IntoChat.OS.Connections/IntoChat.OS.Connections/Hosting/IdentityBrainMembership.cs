using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.Extensions.Options;
using Orleans;

namespace IntoChat.OS.Connections.Hosting;

// Kernel.Brains() over the identity directory grain the host names (IdentityHostOptions.DirectoryKey).
internal sealed class IdentityBrainMembership(IGrainFactory grains, IOptions<IdentityHostOptions>? identity = null) : Framework.IBrainMembership
{
    public Task<string[]> BrainsOf(string personId, CancellationToken cancellationToken = default)
        => grains.GetGrain<IIdentityDirectory>((identity?.Value ?? new()).DirectoryKey).BrainsOfAsync(personId, cancellationToken);
}
