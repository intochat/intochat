using DigitalBrain.Framework;
using System.Text.Json;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using DigitalBrain.Platform.Integrations;
using DigitalBrain.Platform.Integrations.Accounts;
using DigitalBrain.Testing.Module;
using Xunit;

namespace DigitalBrain.Platform.Tests.Unit.Integrations;

public sealed class ConnectionCompatibilityFacts
{
    [Fact]
    public void PreRenameJsonRetainsCredentialReferencesAndProvisioningRevision()
    {
        const string connectionsJson = """
            {"Connections":{"mail":{"Id":"mail","IntegrationId":"gmail","WorkspaceId":"owner",
            "Credential":{"Reference":"secret://owner/mail","Label":"Mailbox","Status":1},"Status":0,"LastProbedAt":null}}}
            """;
        var connections = JsonSerializer.Deserialize<ConnectionsState>(connectionsJson)!;
        var connection = Assert.Single(connections.Connections).Value;
        Assert.Equal("gmail", connection.IntegrationId);
        Assert.Equal("owner", connection.WorkspaceId);
        Assert.Equal("secret://owner/mail", connection.Credential.Reference);
        Assert.Equal("", connection.AuthMethodId);
        Assert.Equal(ConnectionStatus.Connected, connection.Status);
        Assert.Contains("\"IntegrationId\"", JsonSerializer.Serialize(connections));
        const string integrationJson = """
            {"References":{"ApiKey":{"Reference":"secret://%3Aintegrations/integration.example.ApiKey","Label":"Key","Status":1}},
            "Settings":{"Endpoint":"https://example.test"},"Revision":7}
            """;
        var integration = JsonSerializer.Deserialize<IntegrationState>(integrationJson)!;
        Assert.Equal(7, integration.Revision);
        Assert.Equal("https://example.test", integration.Settings["Endpoint"]);
        Assert.Equal("secret://%3Aintegrations/integration.example.ApiKey", integration.References["ApiKey"].Reference);
    }

    [Fact]
    public async Task ConnectionReactivationPreservesAuthorizationAndSecretReference()
    {
        var ct = TestContext.Current.CancellationToken;
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "owner",
            AccountId = "owner",
            BrainId = "owner",
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        try
        {
            await using var brain = await ModuleTest.Create().WithModule<ConnectionTestModule>().StartAsync(ct);
            var registry = brain.Get<IConnectionRegistry>("owner");
            var saved = await registry.Connect(new ConnectConnection
            {
                IntegrationId = "gmail",
                ConnectionId = "mail",
                AuthMethodId = "api-key",
                Value = "test-only-credential",
            }, ct);
            await brain.DeactivateAsync(registry, ct);
            var restored = Assert.Single(await registry.List(ct));
            Assert.Equal(saved, restored);
            Assert.Equal("api-key", restored.AuthMethodId);
            Assert.DoesNotContain("test-only-credential", JsonSerializer.Serialize(restored));
        }
        finally { Orleans.Runtime.RequestContext.Clear(); }
    }
}
