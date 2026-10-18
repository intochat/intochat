using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Authority.Identity.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

// The deployment states its identity under IntoChatOS:Identity; the AppHost projects those
// keys to every resource that serves or shares the session, so silo and clients agree.
internal static class HttpIdentityProjection
{
    public static IdentityHostOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(IdentityHostOptions.SectionName);
        if (!section.Exists())
        { throw new InvalidOperationException($"{IdentityHostOptions.SectionName} is required to host an HTTP identity."); }
        var identity = section.Get<IdentityHostOptions>() ?? new();
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
        })
        {
            resource.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IdentityHostOptions.Key(name)), value);
        }
    }
}
