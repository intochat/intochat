using IntoChat.OS.Authority.Identity.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class IdentityHostOptionsFacts
{
    [Fact]
    public void TheHostAppSettingsCarryTheDistributionIdentity()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        Assert.Equal("intochat.session", configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.CookieName))]);
        Assert.Equal("IntoChat.v1", configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionApplicationName))]);
        Assert.Equal("intochat-protection-v1", configuration[IdentityHostOptions.Key(nameof(IdentityHostOptions.ProtectionContainerName))]);
    }

    // The orchestrated deployment (AppHost) and the standalone image (OS appsettings) each state
    // the identity; the AppHost's env projection shadows the image's copy at runtime, so a drift
    // between them would be invisible there. This fact is where it becomes visible.
    [Fact]
    public void TheAppHostAndTheImageStateTheSameIdentity()
    {
        using var appHost = new ConfigurationManager();
        appHost.AddJsonFile(Path.Combine(SolutionProjects.DirectoryOf("IntoChat.OS.AppHost"), "appsettings.json"));
        using var image = new ConfigurationManager();
        image.AddJsonFile(Path.Combine(SolutionProjects.DirectoryOf("IntoChat.OS.Kernel"), "appsettings.json"));
        foreach (var key in new[]
        {
            nameof(IdentityHostOptions.CookieName),
            nameof(IdentityHostOptions.ProtectionApplicationName),
            nameof(IdentityHostOptions.ProtectionContainerName),
        })
        {
            Assert.Equal(image[IdentityHostOptions.Key(key)], appHost[IdentityHostOptions.Key(key)]);
        }
    }
}
