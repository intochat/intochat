using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Testing;
using IntoChat.OS.Tests.E2E.Fixtures.Doubles;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier3_CrossFeature;

public sealed class ConnectionWebhookFacts : E2ETestBase
{
    [Fact]
    public async Task T3_Connection_Webhook_LifecycleProgression_ProjectsStateTransitionsAndPreservesMetadata()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        WebhookSimulator.Register(os);

        var brainId = "test-webhook-lifecycle";
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", new BrainId(brainId)));
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));

        // 0. Initial state
        var initial = await catalogNeuron.Read();
        Assert.Equal(0L, initial.Revision);
        Assert.Empty(initial.Rows);

        var t1 = DateTimeOffset.UtcNow;
        var connectionKey = ConnectionKeys.For(brainId, "github", "c1");

        // 1. Connected
        await WebhookSimulator.SimulateConnected(os, brain, "github", "c1", authMethodId: "oauth", account: "octocat", probedAt: t1);
        var s1 = await catalogNeuron.Read();
        Assert.Equal(1L, s1.Revision);
        Assert.Single(s1.Rows);
        var row1 = s1.RowOf(connectionKey);
        Assert.NotNull(row1);
        Assert.Equal(ConnectionStatus.Connected, row1.Status);
        Assert.Equal("octocat", row1.Account);
        Assert.Equal(t1, row1.LastProbedAt);

        // 2. Failing (preserves Account)
        var t2 = t1.AddMinutes(5);
        await WebhookSimulator.SimulateFailing(os, brain, "github", "c1", detail: "503 Service Unavailable", probedAt: t2);
        var s2 = await catalogNeuron.Read();
        Assert.Equal(2L, s2.Revision);
        var row2 = s2.RowOf(connectionKey);
        Assert.NotNull(row2);
        Assert.Equal(ConnectionStatus.Failing, row2.Status);
        Assert.Equal("octocat", row2.Account);
        Assert.Equal(t2, row2.LastProbedAt);

        // 3. Expired (preserves Account)
        var t3 = t2.AddMinutes(5);
        await WebhookSimulator.SimulateExpired(os, brain, "github", "c1", probedAt: t3);
        var s3 = await catalogNeuron.Read();
        Assert.Equal(3L, s3.Revision);
        var row3 = s3.RowOf(connectionKey);
        Assert.NotNull(row3);
        Assert.Equal(ConnectionStatus.Expired, row3.Status);
        Assert.Equal("octocat", row3.Account);

        // 4. Reconnected
        var t4 = t3.AddMinutes(5);
        await WebhookSimulator.SimulateConnected(os, brain, "github", "c1", authMethodId: "oauth", account: "octocat", probedAt: t4);
        var s4 = await catalogNeuron.Read();
        Assert.Equal(4L, s4.Revision);
        var row4 = s4.RowOf(connectionKey);
        Assert.NotNull(row4);
        Assert.Equal(ConnectionStatus.Connected, row4.Status);

        // 5. Disconnected (removes row from catalog)
        await WebhookSimulator.SimulateDisconnected(os, brain, "github", "c1");
        var s5 = await catalogNeuron.Read();
        Assert.Equal(5L, s5.Revision);
        Assert.Empty(s5.Rows);
        Assert.Null(s5.RowOf(connectionKey));
    }

    [Fact]
    public async Task T3_Connection_Webhook_DuplicateFacts_DropSilently_WithoutRevisionBumpOrSpuriousSignals()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        WebhookSimulator.Register(os);

        var brainId = "test-webhook-dedup";
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", new BrainId(brainId)));
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));
        await catalogNeuron.Read();

        var t1 = DateTimeOffset.UtcNow;

        // First delivery
        await WebhookSimulator.SimulateConnected(os, brain, "slack", "s1", authMethodId: "oauth", account: "workspace-admin", probedAt: t1);
        var s1 = await catalogNeuron.Read();
        Assert.Equal(1L, s1.Revision);
        Assert.Single(s1.Rows);

        // Duplicate delivery with identical payload
        await WebhookSimulator.SimulateConnected(os, brain, "slack", "s1", authMethodId: "oauth", account: "workspace-admin", probedAt: t1);
        var s2 = await catalogNeuron.Read();

        // Revision must stay at 1L because state did not change
        Assert.Equal(1L, s2.Revision);
        Assert.Single(s2.Rows);
    }

    [Fact]
    public async Task T3_Connection_Webhook_ConcurrentMultiConnectorIngestion_PreservesRevisionMonotonicityAndStateIntegrity()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        WebhookSimulator.Register(os);

        var brainId = "test-webhook-concurrent";
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", new BrainId(brainId)));
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));

        // Activate catalog
        await catalogNeuron.Read();

        string[] connectors = ["github", "slack", "google", "linear", "jira"];
        const int itemsPerConnector = 3; // 15 total items

        var tasks = new List<Task>();
        foreach (var connector in connectors)
        {
            for (var i = 1; i <= itemsPerConnector; i++)
            {
                var connId = $"c_{i}";
                var acct = $"{connector}_user_{i}";
                tasks.Add(WebhookSimulator.SimulateConnected(os, brain, connector, connId, "oauth", acct));
            }
        }

        await Task.WhenAll(tasks);
        await os.Idle();

        var finalCatalog = await catalogNeuron.Read();
        Assert.Equal(15, finalCatalog.Rows.Length);
        Assert.Equal(15L, finalCatalog.Revision);

        // Verify all 15 connection keys exist
        foreach (var connector in connectors)
        {
            for (var i = 1; i <= itemsPerConnector; i++)
            {
                var key = ConnectionKeys.For(brainId, connector, $"c_{i}");
                var row = finalCatalog.RowOf(key);
                Assert.NotNull(row);
                Assert.Equal(ConnectionStatus.Connected, row.Status);
                Assert.Equal($"{connector}_user_{i}", row.Account);
            }
        }
    }

    [Fact]
    public async Task T3_Connection_Webhook_OutOrOrderDelivery_HandlesMissingProbedAtGracefully()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        WebhookSimulator.Register(os);

        var brainId = "test-webhook-timestamps";
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", new BrainId(brainId)));
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));
        await catalogNeuron.Read();

        var t1 = DateTimeOffset.UtcNow;
        var connectionKey = ConnectionKeys.For(brainId, "google", "g1");

        // Connected with explicit timestamp
        await WebhookSimulator.SimulateConnected(os, brain, "google", "g1", "oauth", "guser", probedAt: t1);
        var s1 = await catalogNeuron.Read();
        Assert.Equal(t1, s1.RowOf(connectionKey)?.LastProbedAt);

        // Failing arrives with null probedAt -> existing LastProbedAt must be preserved
        await WebhookSimulator.SimulateFailing(os, brain, "google", "g1", detail: "rate-limited", probedAt: null);
        var s2 = await catalogNeuron.Read();
        Assert.Equal(t1, s2.RowOf(connectionKey)?.LastProbedAt);
    }

    [Fact]
    public async Task T3_Connection_Webhook_ReorderInteraction_PreservesCustomDisplayOrderAcrossStatusUpdates()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        WebhookSimulator.Register(os);

        var brainId = "test-webhook-reorder";
        using var person = os.Edge.Enter(os.Edge.Person("alice", "acct_1", new BrainId(brainId)));
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));
        await catalogNeuron.Read();
        var requestsNeuron = os.Get<IConnectionRequests>(IConnectionCatalog.Id(brainId));

        var k1 = ConnectionKeys.For(brainId, "github", "c1");
        var k2 = ConnectionKeys.For(brainId, "slack", "c2");
        var k3 = ConnectionKeys.For(brainId, "google", "c3");

        await WebhookSimulator.SimulateConnected(os, brain, "github", "c1", "oauth", "acct1");
        await WebhookSimulator.SimulateConnected(os, brain, "slack", "c2", "oauth", "acct2");
        await WebhookSimulator.SimulateConnected(os, brain, "google", "c3", "oauth", "acct3");

        // Reorder to [k3, k1, k2]
        string[] customOrder = [k3, k1, k2];
        await requestsNeuron.Reorder(customOrder);

        var afterReorder = await catalogNeuron.Read();
        Assert.Equal(customOrder, afterReorder.Order);
        Assert.Equal(4L, afterReorder.Revision);

        // External webhook delivers Failing for github (k1)
        await WebhookSimulator.SimulateFailing(os, brain, "github", "c1", detail: "auth failed");

        var afterUpdate = await catalogNeuron.Read();
        Assert.Equal(5L, afterUpdate.Revision);
        Assert.Equal(ConnectionStatus.Failing, afterUpdate.RowOf(k1)?.Status);

        // Custom order must remain preserved
        Assert.Equal(customOrder, afterUpdate.Order);
    }
}
