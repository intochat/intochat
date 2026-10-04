using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Aspire.Hosting;

// The deployment's HTTP identity is configuration (DigitalBrain:Identity); the AppHost projects
// it to every resource that serves or shares the session, so silo and clients always agree.
public sealed record HttpIdentity(string CookieName, string ProtectionApplicationName, string ProtectionContainerName)
{
    public const string SectionName = "DigitalBrain:Identity";

    internal static HttpIdentity FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        return new(Required(section, nameof(CookieName)),
            Required(section, nameof(ProtectionApplicationName)),
            Required(section, nameof(ProtectionContainerName)));

        static string Required(IConfiguration section, string key)
            => section[key] is { Length: > 0 } value ? value
                : throw new InvalidOperationException($"{SectionName}:{key} is required to host an HTTP identity.");
    }

    internal void Apply<T>(IResourceBuilder<T> resource)
        where T : IResourceWithEnvironment
    {
        resource.WithEnvironment("DigitalBrain__Identity__" + nameof(CookieName), CookieName);
        resource.WithEnvironment("DigitalBrain__Identity__" + nameof(ProtectionApplicationName), ProtectionApplicationName);
        resource.WithEnvironment("DigitalBrain__Identity__" + nameof(ProtectionContainerName), ProtectionContainerName);
    }
}
