using DigitalBrain.Framework;
using DigitalBrain.Twilio;
using Xunit;

namespace DigitalBrain.Modules.Twilio.Tests.Integration;

public sealed class TwilioEndpointFacts
{
    [Fact]
    public void TheModuleMapsTheStatusWebhook()
        => Assert.True(typeof(IHttpModule).IsAssignableFrom(typeof(TwilioModule)));

    [Fact]
    public void TheConnectorOffersCredentialsAndDeploymentAndRequiresTheDeclaredTwilioIntegration()
    {
        var connector = TwilioModule.Connector;
        var integration = TwilioModule.Integration;
        Assert.Equal("twilio", connector.Id);
        Assert.Equal(["credentials", "deployment"], connector.AuthMethods);
        Assert.Contains(integration.Id, connector.RequiredIntegrations);
        Assert.Empty(connector.OAuthMethods);
        Assert.Contains("AuthToken", integration.SecretFields);
        Assert.DoesNotContain("AccountSid", integration.SecretFields);
        Assert.Equal([typeof(ITwilioSms)], connector.Neurons);
    }

    [Fact]
    public void ABaseUrlOtherThanHttpsRequiresAnExplicitLoopbackChoice()
    {
        var options = new TwilioModuleOptions { BaseUrl = new("http://127.0.0.1:1234/") };
        Assert.Throws<ArgumentException>(options.Validate);
        options.AllowLoopback = true;
        Assert.Equal(options.BaseUrl, options.ResolveBaseUrl());
        options.BaseUrl = new("http://example.com/");
        Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal(TwilioModuleOptions.DefaultBaseUrl, new TwilioModuleOptions().ResolveBaseUrl());
    }
}
