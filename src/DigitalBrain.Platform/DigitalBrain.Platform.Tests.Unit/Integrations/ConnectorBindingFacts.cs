using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using DigitalBrain.Platform.Integrations;
using DigitalBrain.Platform.Integrations.Accounts;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Xunit;

namespace DigitalBrain.Platform.Tests.Unit.Integrations;

public sealed class ConnectorBindingFacts
{
    [Fact]
    public async Task InfrastructureOnlyModulesCanAcquireTheirOwnSchemas()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<InfrastructureModule>().StartAsync(ct);
        var bindings = brain.SiloServices.GetRequiredService<IModuleIntegrations<InfrastructureModule>>();
        Assert.Equal("infrastructure", bindings.Get("infrastructure").IntegrationId);
        Assert.Equal("additional", bindings.Get("additional").IntegrationId);
        Assert.Throws<UnauthorizedAccessException>(() => bindings.Get("other"));
    }

    [Fact]
    public async Task ConnectionProbesRequireTheCallersOwnBrainAndMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ExampleModule>().StartAsync(ct);
        var credentials = brain.SiloServices.GetRequiredService<IModuleConnectionCredentials<ExampleModule>>();
        var caller = new CallerContext { PrincipalId = "owner", AccountId = "owner", BrainId = "a", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp };
        CallerContextStamper.Stamp(caller);
        try
        {
            var scope = BrainScope.Create("owner", "a").Id;
            var key = ConnectionKeys.For(scope, "example", "one");
            await brain.Get<IIntegration>("integration/example").Configure(new() { Values = new() { ["Endpoint"] = "test-endpoint", ["ApiKey"] = "test" } });
            Assert.False(await credentials.IsConnectedAsync(key, ct));
            await brain.Get<IConnectionRegistry>(scope).Connect(new() { IntegrationId = "example", ConnectionId = "one", Value = "secret" }, ct);
            Assert.True(await credentials.IsConnectedAsync(key, ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => credentials.IsConnectedAsync(ConnectionKeys.For(BrainScope.Create("owner", "b").Id, "example", "one"), ct));
            CallerContextStamper.Stamp(caller with { Kind = CallerKind.App, StampedBy = TrustedEdge.AppProxy });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => credentials.IsConnectedAsync(key, ct));
            CallerContextStamper.Stamp(caller with { PrincipalId = "outsider" });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => credentials.IsConnectedAsync(key, ct));
            Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
            await Assert.ThrowsAsync<UntrustedCallerException>(() => credentials.IsConnectedAsync(key, ct));
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    [Fact]
    public async Task CatalogAndConnectFollowAvailabilityAndWarnOnlyOnTransitions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ExampleModule>().StartAsync(ct);
        var logger = new CatalogLogger();
        var catalog = new ConnectorCatalog([ExampleModule.Connector], brain.Grains, logger);
        var grain = brain.Get<IIntegration>("integration/example");
        await grain.Configure(new() { Values = new() { ["Endpoint"] = "test-endpoint", ["ApiKey"] = "test" } });
        var first = Assert.Single(await catalog.ListAsync(ct));
        Assert.Same(first, Assert.Single(await catalog.ListAsync(ct)));
        Assert.Empty(logger.Warnings);
        Assert.Equal(IntegrationStatus.Partial, (await grain.Clear("ApiKey")).Status);
        Assert.Empty(await catalog.ListAsync(ct));
        Assert.Empty(await catalog.ListAsync(ct));
        Assert.Single(logger.Warnings);
        CallerContextStamper.Stamp(new() { PrincipalId = "owner", AccountId = "owner", BrainId = "owner", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        try
        {
            var registry = brain.Get<IConnectionRegistry>(BrainScope.CurrentId());
            var request = new ConnectConnection { IntegrationId = "example", ConnectionId = "one", Value = "secret" };
            await Assert.ThrowsAsync<ArgumentException>(() => registry.Connect(request, ct));
            await grain.Configure(new() { Values = new() { ["Endpoint"] = "test-endpoint", ["ApiKey"] = "again" } });
            Assert.Single(await catalog.ListAsync(ct));
            Assert.Single(logger.Warnings);
            Assert.Equal(ConnectionStatus.Connected, (await registry.Connect(request, ct)).Status);
            await grain.Clear("ApiKey");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => catalog.ListAsync(ct)));
            Assert.Equal(2, logger.Warnings.Count);
        }
        finally { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
    }

    private sealed class CatalogLogger : ILogger<ConnectorCatalog>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        { if (level == LogLevel.Warning) { Warnings.Add(format(state, error)); } }
    }

    public sealed class InfrastructureModule() : Module(new ModuleId("infrastructure"))
    {
        public static IntegrationSchema Integration => IntegrationSchema.For("infrastructure", "Infrastructure");
        public static IntegrationSchema[] Integrations => [IntegrationSchema.For("additional", "Additional")];
        public override void Configure(ISiloBuilder silo) { }
    }

    [Fact]
    public async Task MissingIntegrationHidesConnectorAndProvisioningMakesItAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<ExampleModule>().StartAsync(ct);
        var catalog = brain.SiloServices.GetRequiredService<ConnectorCatalog>();
        Assert.Empty(await catalog.ListAsync(ct));
        var grain = brain.Get<IIntegration>("integration/example");
        await grain.Configure(new ConfigureIntegration { Values = new() { ["Endpoint"] = "test-endpoint", ["ApiKey"] = "test-secret" } });
        Assert.Equal("example", Assert.Single(await catalog.ListAsync(ct)).Id);
        var handle = brain.SiloServices.GetRequiredService<IModuleIntegrations<ExampleModule>>().Get("example");
        var release = await handle.ReleaseAsync(ct);
        Assert.Equal(IntegrationStatus.Ready, release.Snapshot.Status);
        Assert.Equal("test-secret", release.Values["ApiKey"]);
        Assert.DoesNotContain("test-secret", release.ToString());
        Assert.Equal(IntegrationStatus.Partial, (await grain.Clear("ApiKey")).Status);
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
        public static IntegrationSchema Integration => IntegrationSchema.For("example", "Example").RequiresSecret("ApiKey").RequiresSetting("Endpoint");
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
