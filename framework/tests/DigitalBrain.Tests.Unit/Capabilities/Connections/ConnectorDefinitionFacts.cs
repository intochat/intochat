using DigitalBrain.Capabilities.Connections.Connectors;

namespace DigitalBrain.Capabilities.Connections.Tests.Unit;

public sealed class ConnectorDefinitionFacts
{
    private sealed class SampleModule
    {
        public static ConnectorDefinition[] Connectors { get; } =
        [
            new ConnectorDefinition("gmail", "Gmail", ["google"], ["google-oauth"])
        ];
    }

    [Fact]
    public void DeclaredByReadsAStaticConnectorsProperty()
    {
        var connectors = ConnectorDefinition.DeclaredBy(typeof(SampleModule));
        var connector = Assert.Single(connectors);
        Assert.Equal("gmail", connector.Id);
    }

    [Fact]
    public void OAuthMethodMustNameADeclaredIntegration()
    {
        var undeclared = new ConnectorDefinition("gmail", "Gmail", ["google"], [])
        {
            OAuthMethods =
            [
                new OAuthMethod(
                    "google",
                    "https://accounts.google.com/o/oauth2/v2/auth",
                    "https://oauth2.googleapis.com/token",
                    ["openid"],
                    "google-oauth")
            ]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => undeclared.Validate());
        Assert.Contains("google-oauth", ex.Message, StringComparison.Ordinal);

        var declaredRequired = undeclared with
        {
            RequiredIntegrations = ["google-oauth"]
        };
        declaredRequired.Validate();

        var declaredOptional = undeclared with
        {
            OptionalIntegrations = ["google-oauth"]
        };
        declaredOptional.Validate();
    }

    [Fact]
    public void OAuthDeviceMethodValidatesEndpointsClientIdAndOptionalIntegration()
    {
        var publicDevice = new ConnectorDefinition("forge", "Forge", ["device"], [])
        {
            DeviceMethods =
            [
                new OAuthDeviceMethod(
                    "device",
                    "https://forge.example.test/login/device/code",
                    "https://forge.example.test/login/oauth/access_token",
                    ["repo"],
                    DefaultClientId: "public-desktop-client")
            ]
        };
        publicDevice.Validate();

        var missingClientId = new ConnectorDefinition("forge", "Forge", ["device"], [])
        {
            DeviceMethods =
            [
                new OAuthDeviceMethod(
                    "device",
                    "https://forge.example.test/login/device/code",
                    "https://forge.example.test/login/oauth/access_token",
                    ["repo"])
            ]
        };
        Assert.Throws<InvalidOperationException>(() => missingClientId.Validate());

        var undeclaredIntegration = new ConnectorDefinition("forge", "Forge", ["device"], [])
        {
            DeviceMethods =
            [
                new OAuthDeviceMethod(
                    "device",
                    "https://forge.example.test/login/device/code",
                    "https://forge.example.test/login/oauth/access_token",
                    ["repo"],
                    DefaultClientId: "public-desktop-client",
                    IntegrationId: "forge-oauth")
            ]
        };
        Assert.Throws<InvalidOperationException>(() => undeclaredIntegration.Validate());

        var declaredOptionalIntegration = undeclaredIntegration with
        {
            OptionalIntegrations = ["forge-oauth"]
        };
        declaredOptionalIntegration.Validate();
    }
}
