using DigitalBrain.Framework;
using DigitalBrain.RiskSeal;
using Xunit;

namespace DigitalBrain.Modules.RiskSeal.Tests;

public sealed class RiskSealDeclarationFacts
{
    [Fact]
    public void TheModuleMapsNoRoutes()
        => Assert.False(typeof(IHttpModule).IsAssignableFrom(typeof(RiskSealModule)));

    [Fact]
    public void TheConnectorOffersAPastedKeyOrTheDeploymentAndRequiresTheDeclaredIntegration()
    {
        var connector = RiskSealModule.Connector;
        var integration = RiskSealModule.Integration;
        Assert.Equal("riskseal", connector.Id);
        Assert.Equal(["api-key", "deployment"], connector.AuthMethods);
        Assert.Empty(connector.OAuthMethods);
        Assert.Equal([integration.Id], connector.RequiredIntegrations);
        Assert.Contains("ApiKey", integration.SecretFields);
        Assert.Contains("BaseUrl", integration.OptionalSettingFields);
        Assert.Equal([typeof(IRiskSeal)], connector.Neurons);
    }

    [Fact]
    public void ALoopbackBaseUrlRequiresAnExplicitChoice()
    {
        var options = new RiskSealModuleOptions { BaseUrl = new("http://127.0.0.1:1234") };
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.AllowLoopback = true;
        Assert.Equal(new Uri("http://127.0.0.1:1234/"), options.Resolve(null));
        Assert.Throws<InvalidOperationException>(() => options.Resolve("http://example.com/"));
        Assert.Equal(RiskSealModuleOptions.DefaultBaseUrl, new RiskSealModuleOptions().Resolve(null));
    }
}
