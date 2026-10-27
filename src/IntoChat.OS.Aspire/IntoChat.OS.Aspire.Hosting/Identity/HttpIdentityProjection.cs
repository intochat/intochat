using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

// The deployment states its identity under IntoChatOS:Identity; the AppHost projects those
// keys to every resource that serves or shares the session, so silo and clients agree.
internal static class HttpIdentityProjection
{
    public static IdentityHostOptions FromConfiguration(IConfiguration configuration)
    {
        // Absent section: the brand-neutral defaults apply; a distribution pins its own names under this section.
        var identity = configuration.GetSection(IdentityHostOptions.SectionName).Get<IdentityHostOptions>() ?? new();
        if (!identity.Validate())
        { throw new InvalidOperationException($"{IdentityHostOptions.SectionName} must name a cookie, protection application, protection container and session claims."); }
        return identity;
    }

    public static void Apply<T>(this IdentityHostOptions identity, IResourceBuilder<T> resource)
        where T : IResourceWithEnvironment
    {
        foreach (var (name, value) in new[]
        {
            (nameof(IdentityHostOptions.CookieName), identity.CookieName),
            (nameof(IdentityHostOptions.ProtectionApplicationName), identity.ProtectionApplicationName),
            (nameof(IdentityHostOptions.ProtectionContainerName), identity.ProtectionContainerName),
            (nameof(IdentityHostOptions.AccountClaim), identity.AccountClaim),
            (nameof(IdentityHostOptions.BrainClaim), identity.BrainClaim),
            (nameof(IdentityHostOptions.DirectoryKey), identity.DirectoryKey),
        })
        {
            resource.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IdentityHostOptions.Key(name)), value);
        }
    }
}
