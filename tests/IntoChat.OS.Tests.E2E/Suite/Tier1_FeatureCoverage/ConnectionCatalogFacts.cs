using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier1_FeatureCoverage;

public sealed class ConnectionCatalogFacts : E2ETestBase
{
    [Fact]
    public void T1_Connections_CatalogIdentity_ResolvesAtIntegrationsAccountsMain()
    {
        var brainId = "test-alice-brain";
        var id = IConnectionCatalog.Id(brainId);
        var key = IConnectionCatalog.Key(brainId);

        Assert.Equal("integrations.accounts", id.Type);
        Assert.Equal("main", id.Key);
        Assert.Equal(brainId, id.Brain.Value);
        Assert.Equal($"{brainId}/main", key);
    }

    [Fact]
    public async Task T1_Connections_Catalog_InitialRead_ReturnsEmptyRowsAndZeroRevision()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();

        var brainId = "test-alice-brain";
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", new BrainId(brainId)));
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));

        var catalog = await catalogNeuron.Read();

        Assert.NotNull(catalog);
        Assert.Empty(catalog.Rows);
        Assert.Empty(catalog.Pending);
        Assert.Empty(catalog.Order);
        Assert.Equal(0L, catalog.Revision);
    }

    [Fact]
    public async Task T1_Connections_Catalog_OnConnected_UpsertsRowAndAdvancesRevision()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        os.Register<SignalEmitterNeuron>();

        var brainId = "test-alice-brain";
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", new BrainId(brainId)));
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));
        var emitter = os.Get<ISignalEmitter>(new NeuronId(brain, "signal-emitter", "main"));

        // Ensure catalog neuron is activated so brain-wide subscriptions are established
        await catalogNeuron.Read();

        var now = DateTimeOffset.UtcNow;
        // The emitter stands in for a connection neuron, a cluster-internal publisher, so the fact feeds it as the kernel.
        using (os.Edge.EnterKernel(brain)) { await emitter.Connected("github", "conn_1", "oauth", "octocat", now); }
        await os.Idle();

        var catalog = await catalogNeuron.Read();
        Assert.Equal(1L, catalog.Revision);
        Assert.Single(catalog.Rows);

        var expectedKey = ConnectionKeys.For(brainId, "github", "conn_1");
        var stored = catalog.RowOf(expectedKey);
        Assert.NotNull(stored);
        Assert.Equal("github", stored.ConnectorId);
        Assert.Equal("conn_1", stored.ConnectionId);
        Assert.Equal(ConnectionStatus.Connected, stored.Status);
        Assert.Equal("octocat", stored.Account);
        Assert.Equal(now, stored.LastProbedAt);

        // Idempotence check: identical signal does NOT bump revision
        using (os.Edge.EnterKernel(brain)) { await emitter.Connected("github", "conn_1", "oauth", "octocat", now); }
        await os.Idle();
        var unchangedCatalog = await catalogNeuron.Read();
        Assert.Equal(1L, unchangedCatalog.Revision);
    }

    [Fact]
    public async Task T1_Connections_Catalog_OnStatusTransitions_UpdatesRowStatusPreservingMetadata()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        os.Register<SignalEmitterNeuron>();

        var brainId = "test-alice-brain";
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", new BrainId(brainId)));
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));
        var emitter = os.Get<ISignalEmitter>(new NeuronId(brain, "signal-emitter", "main"));

        await catalogNeuron.Read();

        var initialTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var key = ConnectionKeys.For(brainId, "github", "conn_1");

        // Step 1: Initial Connected
        using (os.Edge.EnterKernel(brain)) { await emitter.Connected("github", "conn_1", "oauth", "octocat", initialTime); }
        await os.Idle();

        // Step 2: Transition to Failing
        var failTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        using (os.Edge.EnterKernel(brain)) { await emitter.Failing("github", "conn_1", "Rate limited", failTime); }
        await os.Idle();

        var failingCatalog = await catalogNeuron.Read();
        Assert.Equal(2L, failingCatalog.Revision);
        var failingRow = failingCatalog.RowOf(key);
        Assert.NotNull(failingRow);
        Assert.Equal(ConnectionStatus.Failing, failingRow.Status);
        Assert.Equal("octocat", failingRow.Account); // Metadata preserved
        Assert.Equal(failTime, failingRow.LastProbedAt);

        // Step 3: Transition to Expired
        var expireTime = DateTimeOffset.UtcNow;
        using (os.Edge.EnterKernel(brain)) { await emitter.Expired("github", "conn_1", expireTime); }
        await os.Idle();

        var expiredCatalog = await catalogNeuron.Read();
        Assert.Equal(3L, expiredCatalog.Revision);
        var expiredRow = expiredCatalog.RowOf(key);
        Assert.NotNull(expiredRow);
        Assert.Equal(ConnectionStatus.Expired, expiredRow.Status);
        Assert.Equal("octocat", expiredRow.Account); // Metadata preserved
        Assert.Equal(expireTime, expiredRow.LastProbedAt);
    }

    [Fact]
    public async Task T1_Connections_Catalog_OnDisconnected_RemovesRowFromProjection()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        os.Register<SignalEmitterNeuron>();

        var brainId = "test-alice-brain";
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", new BrainId(brainId)));
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));
        var emitter = os.Get<ISignalEmitter>(new NeuronId(brain, "signal-emitter", "main"));

        await catalogNeuron.Read();

        var key1 = ConnectionKeys.For(brainId, "github", "conn_1");
        var key2 = ConnectionKeys.For(brainId, "slack", "conn_2");

        using (os.Edge.EnterKernel(brain)) { await emitter.Connected("github", "conn_1", "oauth", "octocat"); }
        using (os.Edge.EnterKernel(brain)) { await emitter.Connected("slack", "conn_2", "oauth", "slack_user"); }
        await os.Idle();

        var twoItemCatalog = await catalogNeuron.Read();
        Assert.Equal(2, twoItemCatalog.Rows.Length);
        Assert.Equal(2L, twoItemCatalog.Revision);

        // Disconnect conn_1
        using (os.Edge.EnterKernel(brain)) { await emitter.Disconnected("github", "conn_1"); }
        await os.Idle();

        var afterDisconnect = await catalogNeuron.Read();
        Assert.Equal(3L, afterDisconnect.Revision);
        Assert.Single(afterDisconnect.Rows);
        Assert.Null(afterDisconnect.RowOf(key1));
        Assert.NotNull(afterDisconnect.RowOf(key2));
    }

    [Fact]
    public async Task T1_Connections_Catalog_Reorder_UpdatesCustomDisplayOrder()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        os.Register<SignalEmitterNeuron>();

        var brainId = "test-alice-brain";
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", new BrainId(brainId)));
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));
        var requestsNeuron = os.Get<IConnectionRequests>(IConnectionCatalog.Id(brainId));
        var emitter = os.Get<ISignalEmitter>(new NeuronId(brain, "signal-emitter", "main"));

        await catalogNeuron.Read();

        var k1 = ConnectionKeys.For(brainId, "github", "conn_1");
        var k2 = ConnectionKeys.For(brainId, "slack", "conn_2");
        var k3 = ConnectionKeys.For(brainId, "google", "conn_3");

        using (os.Edge.EnterKernel(brain)) { await emitter.Connected("github", "conn_1", "oauth", "acct1"); }
        using (os.Edge.EnterKernel(brain)) { await emitter.Connected("slack", "conn_2", "oauth", "acct2"); }
        using (os.Edge.EnterKernel(brain)) { await emitter.Connected("google", "conn_3", "oauth", "acct3"); }
        await os.Idle();

        var beforeOrder = await catalogNeuron.Read();
        Assert.Equal(3L, beforeOrder.Revision);

        string[] customOrder = [k3, k1, k2];
        await requestsNeuron.Reorder(customOrder);

        var orderedCatalog = await catalogNeuron.Read();
        Assert.Equal(customOrder, orderedCatalog.Order);
        Assert.Equal(4L, orderedCatalog.Revision);
    }

    [Neuron("signal-emitter")]
    private sealed class SignalEmitterNeuron : Neuron, ISignalEmitter
    {
        public Task Connected(string connectorId, string connectionId, string authMethodId, string? account, DateTimeOffset? probedAt = null)
            => Publish(new ConnectionConnected(connectorId, connectionId, authMethodId, account, probedAt));

        public Task Expired(string connectorId, string connectionId, DateTimeOffset? probedAt = null)
            => Publish(new ConnectionExpired(connectorId, connectionId, probedAt));

        public Task Failing(string connectorId, string connectionId, string detail, DateTimeOffset? probedAt = null)
            => Publish(new ConnectionFailing(connectorId, connectionId, detail, probedAt));

        public Task Disconnected(string connectorId, string connectionId)
            => Publish(new ConnectionDisconnected(connectorId, connectionId));
    }

    private interface ISignalEmitter
    {
        Task Connected(string connectorId, string connectionId, string authMethodId, string? account, DateTimeOffset? probedAt = null);
        Task Expired(string connectorId, string connectionId, DateTimeOffset? probedAt = null);
        Task Failing(string connectorId, string connectionId, string detail, DateTimeOffset? probedAt = null);
        Task Disconnected(string connectorId, string connectionId);
    }
}
