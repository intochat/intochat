using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class IdentityHostOptionsFacts
{
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DigitalBrain.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException($"Repository root (DigitalBrain.slnx) was not found above {AppContext.BaseDirectory}.");
    }

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
        var root = FindRepositoryRoot();
        using var appHost = new ConfigurationManager();
        appHost.AddJsonFile(Path.Combine(root, "src", "DigitalBrain", "AppHost", "DigitalBrain.AppHost", "appsettings.json"));
        using var image = new ConfigurationManager();
        image.AddJsonFile(Path.Combine(root, "src", "DigitalBrain", "OS", "DigitalBrain.OS", "appsettings.json"));
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
