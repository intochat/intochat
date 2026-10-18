using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Authority.Identity.Configuration;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Aspire.Hosting;

// The deployment states its identity under DigitalBrain:Identity; the AppHost projects those
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
        { throw new InvalidOperationException($"{IdentityHostOptions.SectionName} must name a cookie, protection application and protection container."); }
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
        })
        {
            resource.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(IdentityHostOptions.Key(name)), value);
        }
    }
}
