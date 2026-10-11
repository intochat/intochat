using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Integrations;
using DigitalBrain.Framework.Surface;
using DigitalBrain.Framework.Contracts;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Platform.Tests.Unit.Connections;
using Orleans.Hosting;

namespace DigitalBrain.Platform.Tests.Unit.Integrations;

public sealed class ScriptAccountFacts
{
    [Fact]
    public async Task DiscoveryReturnsOnlyTheBrainsConnectionsWithoutCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<ExampleConnectionModule>().StartAsync(ct);
        using var alice = await brain.PersonAsync("alice", ct);
        using var bob = await brain.PersonAsync("bob", ct);
        var scope = alice.BrainId;
        var key = ConnectionKeys.For(scope, "example", "database");
        using (alice.AsCaller())
        {
            await brain.Get<IConnection>(key).Connect("api-key", "private-credential");
            var accounts = brain.Get<IConnectionRequests>(scope);
            var entry = Assert.Single(await accounts.ListConnections());
            Assert.Equal(("example", "database", key, "connected"), (entry.Kind, entry.Id, entry.Neuron, entry.Status));
            Assert.DoesNotContain("private-credential", System.Text.Json.JsonSerializer.Serialize(entry));
            await brain.Get<IConnection>(key).Disconnect();
            Assert.Empty(await accounts.ListConnections());
            await brain.Get<IConnection>(key).Connect("api-key", "expired");
            await brain.Get<IConnection>(key).Probe();
            Assert.Equal("expired", Assert.Single(await accounts.ListConnections()).Status);
        }
        using (bob.AsCaller())
        {
            var surface = new NeuronSurface(brain, new Kernel(brain.Grains), brain.Services.GetRequiredService<NeuronTypeSearch>());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => surface.Call(
                typeof(IConnectionRequests).FullName!, scope, nameof(IConnectionRequests.ListConnections), "[]", ct));
        }
        Assert.Empty(await brain.Get<IConnectionRequests>("brain-two").ListConnections());
    }

    [Fact]
    public async Task RequestingAKnownKindReturnsAnOpaqueRefAndAnUnknownKindIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<PostgresKindModule>().StartAsync(ct);
        var accounts = brain.Get<IConnectionRequests>("brain-1");

        // The host always composes the orleans module, so its connector kind is listed beside the module's.
        var kinds = await accounts.ListKinds();
        Assert.Equal(["orleans", "postgres"], kinds.Select(k => k.Id).Order(StringComparer.Ordinal));
        Assert.Equal("Postgres", kinds.Single(k => k.Id == "postgres").DisplayName);
        var account = await accounts.Request("postgres");
        Assert.Equal("postgres", account.Kind);
        Assert.False(string.IsNullOrWhiteSpace(account.Id));
        Assert.Equal(account, Assert.Single(await accounts.ListPending()));
        await Assert.ThrowsAsync<ArgumentException>(() => accounts.Request("missing"));
    }

    public sealed class PostgresKindModule() : Module(new ModuleId(nameof(PostgresKindModule)))
    {
        public static IntegrationSchema Integration { get; } = IntegrationSchema.For("postgres", "Postgres");
        public static ConnectorDefinition Connector => new("postgres", "Postgres", ["connection-string"], []);

        public override void Configure(ISiloBuilder silo) { }
    }
}
