using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Connections.Hosting;

// Kernel.Brains() over the identity directory grain the host names (IdentityHostOptions.DirectoryKey).
internal sealed class IdentityBrainMembership(IntoChat.OS.Runtime.IStillGrains grains, IOptions<IdentityHostOptions>? identity = null) : Framework.IBrainMembership
{
    public Task<string[]> BrainsOf(string personId, CancellationToken cancellationToken = default)
        => grains.Get<IIdentityDirectory>((identity?.Value ?? new()).DirectoryKey).BrainsOfAsync(personId, cancellationToken);
}
