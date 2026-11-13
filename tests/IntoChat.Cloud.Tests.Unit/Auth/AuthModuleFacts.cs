using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Modules;
using IntoChat.Cloud.Auth.Services;
using AuthModule = IntoChat.Cloud.Auth.AuthModule;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class AuthModuleFacts
{
    [Theory]
    [InlineData("cloud", true)]
    [InlineData("local", false)]
    [InlineData("local+cloud", false)]
    [InlineData(null, false)]
    public void OnlyCloudLoadsAuth(string? topology, bool loads) => Assert.Equal(loads, AuthModule.LoadsUnder(topology));

    [Fact]
    public void TheManifestRequiresIdentityAndDoesNotProvideIt()
    {
        var manifest = ModuleManifest.Of(typeof(AuthModule));
        Assert.Equal("auth", manifest.Id);
        Assert.Contains(manifest.Requires, capability => capability == Capability.Identity.V1);
        Assert.Contains(manifest.Requires, capability => capability == Capability.Secrets.V1);
        Assert.DoesNotContain(manifest.Provides, capability => capability == Capability.Identity.V1);
        Assert.Contains(manifest.Secrets, secret => secret.Name == AuthSecrets.HashingKey);
    }

    [Fact]
    public void DescribeNamesTheAccountSurfaceTheSettingsTabMounts()
    {
        var provider = new AuthIdentityProvider(null!, null!, new AuthModuleOptions());
        var described = provider.Describe();
        Assert.Equal(AuthIdentityProvider.AccountSurfaceId, described.AccountSurface);
        Assert.Equal("account/view/surface", described.AccountSurface.ToString());
        Assert.Equal("view|surface", AuthIdentityProvider.AccountSurfaceSetting);
    }
}
