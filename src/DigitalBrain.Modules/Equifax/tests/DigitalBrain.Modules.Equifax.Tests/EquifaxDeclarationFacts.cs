using DigitalBrain.Equifax;
using DigitalBrain.Equifax.Signals;
using DigitalBrain.Framework;
using Xunit;

namespace DigitalBrain.Modules.Equifax.Tests;

public sealed class EquifaxDeclarationFacts
{
    [Fact]
    public void TheModuleMapsNoRoutes()
        => Assert.False(typeof(IHttpModule).IsAssignableFrom(typeof(EquifaxModule)));

    [Fact]
    public void TheConnectorRequiresTheDeclaredIntegrationAndOffersPastedOrDeploymentCredentials()
    {
        var connector = EquifaxModule.Connector;
        var integration = EquifaxModule.Integration;
        Assert.Equal("equifax", connector.Id);
        Assert.Equal([integration.Id], connector.RequiredIntegrations);
        Assert.Equal(["credentials", "deployment"], connector.AuthMethods);
        Assert.Empty(connector.OAuthMethods);
        Assert.Equal(["ClientId", "ClientSecret"], integration.SecretFields);
        Assert.Equal(["BaseUrl"], integration.OptionalSettingFields);
        Assert.Equal([typeof(IEquifax)], connector.Neurons);
        Assert.Equal([typeof(BureauReportRead), typeof(BureauReportFailed)], connector.Signals);
    }

    [Fact]
    public void ALoopbackBaseUrlRequiresAnExplicitChoice()
    {
        var options = new EquifaxModuleOptions { BaseUrl = new("http://127.0.0.1:1234/") };
        Assert.Throws<ArgumentException>(options.Validate);
        options.AllowLoopback = true;
        options.Validate();
        Assert.Throws<ArgumentException>(() => options.Resolve(new("http://example.com/")));
        Assert.Equal(EquifaxModuleOptions.DefaultBaseUrl, new EquifaxModuleOptions().Resolve(null));
    }
}
