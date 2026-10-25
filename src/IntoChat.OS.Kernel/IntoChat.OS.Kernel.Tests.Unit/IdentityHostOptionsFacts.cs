using IntoChat.OS.Connections.Identity.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class IdentityHostOptionsFacts
{
    // The OS ships no distribution identity: the brand-neutral defaults in IdentityHostOptions apply and a
    // distribution (the platform) states its own names under the identity section.
    [Fact]
    public void TheShippedAppSettingsStateNoIdentity()
    {
        foreach (var project in new[] { "IntoChat.OS.Kernel", "IntoChat.OS.AppHost" })
        {
            using var configuration = new ConfigurationManager();
            configuration.AddJsonFile(Path.Combine(SolutionProjects.DirectoryOf(project), "appsettings.json"));
            Assert.False(configuration.GetSection(IdentityHostOptions.SectionName).Exists(), project);
        }
    }
}
