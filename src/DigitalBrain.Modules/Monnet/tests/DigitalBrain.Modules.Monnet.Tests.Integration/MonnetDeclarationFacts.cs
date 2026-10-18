using DigitalBrain.Framework;
using DigitalBrain.Monnet;
using Xunit;

namespace DigitalBrain.Modules.Monnet.Tests.Integration;

public sealed class MonnetDeclarationFacts
{
    [Fact]
    public void TheModuleMapsTheWebhookRoute()
        => Assert.True(typeof(IHttpModule).IsAssignableFrom(typeof(MonnetModule)));

    [Fact]
    public void TheConnectorRequiresTheDeclaredMonnetIntegration()
    {
        var connector = MonnetModule.Connector;
        var integration = MonnetModule.Integration;

        Assert.Equal("monnet", connector.Id);
        Assert.Equal(["credentials", "deployment"], connector.AuthMethods);
        Assert.Equal([integration.Id], connector.RequiredIntegrations);
        Assert.Empty(connector.OAuthMethods);
        Assert.Equal([typeof(IMonnet)], connector.Neurons);
        Assert.Equal(["ApiKey"], integration.SecretFields);
        Assert.Equal(["MerchantId"], integration.SettingFields);
        Assert.Equal(["WebhookSecret"], integration.OptionalSecretFields);
        Assert.Equal(["BaseUrl"], integration.OptionalSettingFields);
    }

    [Fact]
    public void ALoopbackBaseUrlRequiresAnExplicitChoice()
    {
        var local = new Uri("http://127.0.0.1:1234/");
        Assert.Throws<MonnetUnavailableException>(() => new MonnetModuleOptions { BaseUrl = local }.Validate());
        Assert.Equal(local, new MonnetModuleOptions { BaseUrl = local, AllowLoopback = true }.Resolve(null));
        Assert.Throws<MonnetUnavailableException>(() => new MonnetModuleOptions { AllowLoopback = true }.Resolve(new("http://example.com/")));
        Assert.Equal(MonnetModuleOptions.DefaultBaseUrl, new MonnetModuleOptions().Resolve(null));
    }

    [Theory]
    [InlineData("MerchantId=m;ApiKey=k", true)]
    [InlineData(" merchantid = m ; apikey = k ; BaseUrl = https://api.monnet.example/ ", true)]
    [InlineData("ApiKey=k", false)]
    [InlineData("MerchantId=m;ApiKey=k;Other=x", false)]
    [InlineData("just-a-key", false)]
    public void PastedCredentialsReadLikeAConnectionString(string value, bool valid)
        => Assert.Equal(valid, MonnetCredentials.TryParse(value, out _));
}
