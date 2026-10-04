using Microsoft.Extensions.Configuration;

namespace DigitalBrain.OS.Tests.Unit;

public sealed class IdentityHostOptionsFacts
{
    [Fact]
    public void TheHostAppSettingsCarryTheDistributionIdentity()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        Assert.Equal("intochat.session", configuration["DigitalBrain:Identity:CookieName"]);
        Assert.Equal("IntoChat.v1", configuration["DigitalBrain:Identity:ProtectionApplicationName"]);
        Assert.Equal(IntoChat.IntoChatConfiguration.ProtectionContainerName, configuration["DigitalBrain:Identity:ProtectionContainerName"]);
    }
}
