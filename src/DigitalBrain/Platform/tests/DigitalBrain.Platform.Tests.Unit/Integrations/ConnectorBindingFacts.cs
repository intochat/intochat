using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Xunit;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using DigitalBrain.Platform.Integrations.Accounts;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Platform.Tests.Unit.Integrations;

public sealed class ConnectorBindingFacts
{
    [Fact]
    public async Task MissingIntegrationHidesConnectorAndProvisioningMakesItAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ExampleModule>().StartAsync(ct);
        var catalog = brain.SiloServices.GetRequiredService<ConnectorCatalog>();
        Assert.Empty(await catalog.ListAsync(ct));
        var grain = brain.Get<IIntegration>("integration/example");
        await grain.Configure(new ConfigureIntegration { Values = new() { ["ApiKey"] = "test-secret" } });
        Assert.Equal("example", Assert.Single(await catalog.ListAsync(ct)).Id);
        var handle = brain.SiloServices.GetRequiredService<IModuleIntegrations<ExampleModule>>().Get("example");
        var release = await handle.ReleaseAsync(ct);
        Assert.Equal(IntegrationStatus.Ready, release.Snapshot.Status);
        Assert.Equal("test-secret", release.Values["ApiKey"]);
        Assert.DoesNotContain("test-secret", release.ToString());
        await grain.Clear("ApiKey");
        Assert.Empty(await catalog.ListAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle.ReadSecretAsync("ApiKey", ct));
    }

    [Fact]
    public void LegacyConnectionGetsOnlyAnUnambiguousAuthenticationMethod()
    {
        var legacy = new Connection { Id = "one", IntegrationId = "example", WorkspaceId = "brain", Credential = new(), Status = ConnectionStatus.Connected };
        var normalized = ConnectionAuthentication.Normalize(legacy, [ExampleModule.Connector]);
        Assert.Equal("api-key", normalized.AuthMethodId);
        Assert.Equal(ConnectionStatus.Connected, normalized.Status);
        Assert.Equal(ConnectionStatus.Disconnected, ConnectionAuthentication.Normalize(legacy,
            [ExampleModule.Connector with { AuthMethods = ["api-key", "oauth"] }]).Status);
    }

    public sealed class ExampleModule() : Module(new ModuleId("example"))
    {
        public static IntegrationSchema Integration => IntegrationSchema.For("example", "Example").RequiresSecret("ApiKey");
        public static ConnectorDefinition Connector => new("example", "Example", ["api-key"], ["example"]);
        public override void Configure(ISiloBuilder silo) { }
    }

    [Fact]
    public void ModuleCannotAcquireAnUndeclaredIntegration()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var bindings = new ModuleIntegrations<ExampleModule>(services, new ModuleInventory([typeof(ExampleModule)]));
        Assert.Throws<UnauthorizedAccessException>(() => bindings.Get("other"));
    }

    [Fact]
    public void UncomposedModuleCannotAcquireEvenItsDeclaredIntegration()
    {
        var bindings = new ModuleIntegrations<ExampleModule>(new ServiceCollection().BuildServiceProvider(), new ModuleInventory([]));
        Assert.Throws<UnauthorizedAccessException>(() => bindings.Get("example"));
    }

    [Fact]
    public void ConnectionKeysSeparateBrainsConnectorsAndEscapedSegments()
    {
        Assert.NotEqual(ConnectionKeys.For("a", "gmail", "b/c"), ConnectionKeys.For("a/b", "gmail", "c"));
        Assert.NotEqual(ConnectionKeys.For("a", "gmail", "c"), ConnectionKeys.For("b", "gmail", "c"));
        Assert.Equal("connection/a/gmail/b%2Fc", ConnectionKeys.For("a", "gmail", "b/c"));
    }

    [Fact]
    public void ConnectorDependenciesMustHaveProvisioningSchemas()
    {
        Assert.Throws<InvalidOperationException>(() => ConnectorDiscovery.Validate([ExampleModule.Connector], []));
        ConnectorDiscovery.Validate([ExampleModule.Connector], [ExampleModule.Integration]);
    }
}
