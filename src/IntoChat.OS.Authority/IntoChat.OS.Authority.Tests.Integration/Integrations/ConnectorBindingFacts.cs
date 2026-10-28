using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using IntoChat.OS.Authority.Integrations;
using IntoChat.OS.Authority.Tests.Integration.Connections;
using IntoChat.OS.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority.Tests.Integration.Integrations;

[Trait("host", "own")]
public sealed class ConnectorBindingFacts
{
    [Fact]
    public async Task InfrastructureOnlyModulesCanAcquireTheirOwnSchemas()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<InfrastructureModule>().StartAsync(ct);
        var bindings = brain.Services.GetRequiredService<IModuleIntegrations<InfrastructureModule>>();
        Assert.Equal("infrastructure", bindings.Get("infrastructure").IntegrationId);
        Assert.Equal("additional", bindings.Get("additional").IntegrationId);
        Assert.Throws<UnauthorizedAccessException>(() => bindings.Get("other"));
    }

    [Fact]
    public async Task CatalogAndTheConnectRouteFollowAvailabilityAndWarnOnlyOnTransitions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<ExampleConnectionModule>().StartAsync(ct);
        Task<HttpResponseMessage> Connect() => brain.HttpClient.PostAsJsonAsync("/brains/owner/connections/one/connect",
            new { connectorId = "example", authMethodId = "api-key", value = "secret-value-7d1" }, ct);
        var logger = new CatalogLogger();
        var catalog = new ConnectorCatalog([ExampleConnectionModule.Connector], brain.Platform, logger);
        var grain = brain.Platform.Integration("example-oauth");
        await grain.Configure(new() { Values = new() { ["ClientId"] = "test-client", ["ClientSecret"] = "test" } });
        var first = Assert.Single(await catalog.ListAsync(ct));
        Assert.Same(first, Assert.Single(await catalog.ListAsync(ct)));
        Assert.Empty(logger.Warnings);
        Assert.Equal(IntegrationStatus.Partial, (await grain.Clear("ClientId")).Status);
        Assert.Empty(await catalog.ListAsync(ct));
        Assert.Empty(await catalog.ListAsync(ct));
        Assert.Single(logger.Warnings);

        // A connector the catalog hides takes no pasted value; the refusal names it and echoes nothing.
        using (var refused = await Connect())
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync(ct);
            Assert.Contains("Connector 'example' is not offered by this deployment.", body, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value-7d1", body, StringComparison.Ordinal);
        }
        Assert.Empty((await brain.HttpClient.GetFromJsonAsync<JsonElement>("/brains/owner/connections", ct)).EnumerateArray());

        await grain.Configure(new() { Values = new() { ["ClientId"] = "again" } });
        Assert.Single(await catalog.ListAsync(ct));
        Assert.Single(logger.Warnings);
        using (var accepted = await Connect())
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal("Connected", (await accepted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("status").GetString());
        }
        await grain.Clear("ClientId");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => catalog.ListAsync(ct)));
        Assert.Equal(2, logger.Warnings.Count);
    }

    private sealed class CatalogLogger : ILogger<ConnectorCatalog>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        { if (level == LogLevel.Warning) { Warnings.Add(format(state, error)); } }
    }

    [DigitalBrain.Module("infrastructure")]
    public sealed class InfrastructureModule : DigitalBrain.Module
    {
        public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [], []);
        public static IntegrationSchema Integration => IntegrationSchema.For("infrastructure", "Infrastructure");
        public static IntegrationSchema[] Integrations => [IntegrationSchema.For("additional", "Additional")];
    }

    [Fact]
    public async Task MissingIntegrationHidesConnectorAndProvisioningMakesItAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<ExampleModule>().StartAsync(ct);
        var catalog = brain.Services.GetRequiredService<ConnectorCatalog>();
        // The host always composes the orleans module: its connector is the one offered before provisioning.
        async Task<string[]> Offered() => [.. (await catalog.ListAsync(ct)).Select(entry => entry.Id).Order(StringComparer.Ordinal)];
        Assert.Equal(["orleans"], await Offered());
        var grain = brain.Platform.Integration("example");
        await grain.Configure(new ConfigureIntegration { Values = new() { ["Endpoint"] = "test-endpoint", ["ApiKey"] = "test-secret" } });
        Assert.Equal(["example", "orleans"], await Offered());
        var handle = brain.Services.GetRequiredService<IModuleIntegrations<ExampleModule>>().Get("example");
        var release = await handle.ReleaseAsync(ct);
        Assert.Equal(IntegrationStatus.Ready, release.Snapshot.Status);
        Assert.Equal("test-secret", release.Values["ApiKey"]);
        Assert.DoesNotContain("test-secret", release.ToString());
        Assert.Equal(IntegrationStatus.Partial, (await grain.Clear("ApiKey")).Status);
        Assert.Equal(["orleans"], await Offered());
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle.ReadSecretAsync("ApiKey", ct));
    }

    [DigitalBrain.Module("example")]
    public sealed class ExampleModule : DigitalBrain.Module
    {
        public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [], []);
        public static IntegrationSchema Integration => IntegrationSchema.For("example", "Example").RequiresSecret("ApiKey").RequiresSetting("Endpoint");
        public static ConnectorDefinition Connector => new("example", "Example", ["api-key"], ["example"]);
    }

    [Fact]
    public void ModuleCannotAcquireAnUndeclaredIntegration()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var bindings = new ModuleIntegrations<ExampleModule>(services, new ModuleInventory([OsModule.FromDigital(typeof(ExampleModule))]));
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
        Assert.Throws<ArgumentException>(() => ConnectionKeys.For("a/b", "gmail", "c"));
        Assert.NotEqual(ConnectionKeys.For("a", "gmail", "c"), ConnectionKeys.For("b", "gmail", "c"));
        Assert.Equal("a/connection/gmail/b%2Fc", ConnectionKeys.For("a", "gmail", "b/c"));
    }

    [Fact]
    public void ConnectorDependenciesMustHaveProvisioningSchemas()
    {
        Assert.Throws<InvalidOperationException>(() => ConnectorDiscovery.Validate([ExampleModule.Connector], []));
        ConnectorDiscovery.Validate([ExampleModule.Connector], [ExampleModule.Integration]);
    }
}
