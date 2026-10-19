using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Identity;

public static class BrainAccess
{
    public static IBrainAccess Directory(IIdentityClient identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new Directory.DirectoryBrainAccess(identity);
    }

    // The platform's own access under the configured posture.
    public static IBrainAccess Local(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return IdentityHosting.LocalBrainAccess(services);
    }
}
