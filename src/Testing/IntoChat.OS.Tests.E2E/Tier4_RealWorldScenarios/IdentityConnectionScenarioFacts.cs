using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Testing;
using IntoChat.OS.Tests.E2E.Fixtures.Doubles;
using IntoChat.OS.Tests.E2E.Harness;
using Xunit;

namespace IntoChat.OS.Tests.E2E.Tier4_RealWorldScenarios;

/// <summary>
/// Tier 4 Real-World Application Scenarios: Identity Connection Lifecycle &amp; Webhook Ingestion.
/// Validates the complete user journey from external OAuth webhook ingestion to catalog CQRS projection,
/// token expiration, re-authentication, metadata preservation, user-driven catalog reordering, and deduplication.
/// </summary>
public sealed class IdentityConnectionScenarioFacts : E2ETestBase
{
    [Fact]
    public async Task T4_IdentityConnection_FullOAuthLifecycleJourney_FromIngestionToTokenExpiryAndDisconnection()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        WebhookSimulator.Register(os);

        var brainId = "alice-oauth-lifecycle";
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));

        // Step 1: Initial empty state
        var initial = await catalogNeuron.Read();
        Assert.Equal(0L, initial.Revision);
        Assert.Empty(initial.Rows);

        var connectionKey = ConnectionKeys.For(brainId, "github", "octo_conn");
        var t1 = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

        // Step 2: External OAuth webhook fact ingestion (GitHub account "octocat")
        await WebhookSimulator.SimulateConnected(os, brain, "github", "octo_conn", authMethodId: "oauth", account: "octocat", probedAt: t1);
        var stateAfterConnect = await catalogNeuron.Read();
        Assert.Equal(1L, stateAfterConnect.Revision);
        Assert.Single(stateAfterConnect.Rows);

        var rowConnected = stateAfterConnect.RowOf(connectionKey);
        Assert.NotNull(rowConnected);
        Assert.Equal("github", rowConnected.ConnectorId);
        Assert.Equal("octo_conn", rowConnected.ConnectionId);
        Assert.Equal(ConnectionStatus.Connected, rowConnected.Status);
        Assert.Equal("octocat", rowConnected.Account);
        Assert.Equal(t1, rowConnected.LastProbedAt);

        // Step 3: Subsequent token refresh fact with updated probe time
        var t2 = t1.AddHours(1);
        await WebhookSimulator.SimulateConnected(os, brain, "github", "octo_conn", authMethodId: "oauth", account: "octocat", probedAt: t2);
        var stateAfterRefresh = await catalogNeuron.Read();
        Assert.Equal(2L, stateAfterRefresh.Revision);
        var rowRefreshed = stateAfterRefresh.RowOf(connectionKey);
        Assert.NotNull(rowRefreshed);
        Assert.Equal(ConnectionStatus.Connected, rowRefreshed.Status);
        Assert.Equal(t2, rowRefreshed.LastProbedAt);

        // Step 4: Token expires -> transition to Expired, strictly preserving account metadata
        var t3 = t2.AddHours(2);
        await WebhookSimulator.SimulateExpired(os, brain, "github", "octo_conn", probedAt: t3);
        var stateAfterExpire = await catalogNeuron.Read();
        Assert.Equal(3L, stateAfterExpire.Revision);

        var rowExpired = stateAfterExpire.RowOf(connectionKey);
        Assert.NotNull(rowExpired);
        Assert.Equal(ConnectionStatus.Expired, rowExpired.Status);
        Assert.Equal("octocat", rowExpired.Account); // Metadata preserved
        Assert.Equal(t3, rowExpired.LastProbedAt);

        // Step 5: User re-authorizes integration -> transition back to Connected
        var t4 = t3.AddMinutes(30);
        await WebhookSimulator.SimulateConnected(os, brain, "github", "octo_conn", authMethodId: "oauth", account: "octocat", probedAt: t4);
        var stateAfterReauth = await catalogNeuron.Read();
        Assert.Equal(4L, stateAfterReauth.Revision);

        var rowReauthed = stateAfterReauth.RowOf(connectionKey);
        Assert.NotNull(rowReauthed);
        Assert.Equal(ConnectionStatus.Connected, rowReauthed.Status);
        Assert.Equal(t4, rowReauthed.LastProbedAt);

        // Step 6: User revokes connection -> removed from projection
        await WebhookSimulator.SimulateDisconnected(os, brain, "github", "octo_conn");
        var stateAfterDisconnect = await catalogNeuron.Read();
        Assert.Equal(5L, stateAfterDisconnect.Revision);
        Assert.Empty(stateAfterDisconnect.Rows);
        Assert.Null(stateAfterDisconnect.RowOf(connectionKey));
    }

    [Fact]
    public async Task T4_IdentityConnection_MultiConnectorCoexistence_AndUserCatalogReorderingJourney()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        WebhookSimulator.Register(os);

        var brainId = "bob-multi-connector";
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));
        var requestsNeuron = os.Get<IConnectionRequests>(IConnectionCatalog.Id(brainId));
        await catalogNeuron.Read();

        var ghKey = ConnectionKeys.For(brainId, "github", "gh_1");
        var gdriveKey = ConnectionKeys.For(brainId, "google", "g_1");
        var slackKey = ConnectionKeys.For(brainId, "slack", "s_1");

        // 1. Establish 3 distinct connectors
        await WebhookSimulator.SimulateConnected(os, brain, "github", "gh_1", "oauth", "bob-dev");
        await WebhookSimulator.SimulateConnected(os, brain, "google", "g_1", "oauth", "bob@gmail.com");
        await WebhookSimulator.SimulateConnected(os, brain, "slack", "s_1", "oauth", "bob-slack");

        var catalog3 = await catalogNeuron.Read();
        Assert.Equal(3, catalog3.Rows.Length);
        Assert.Equal(3L, catalog3.Revision);
        Assert.True(catalog3.Resolved("github"));
        Assert.True(catalog3.Resolved("google"));
        Assert.True(catalog3.Resolved("slack"));

        // 2. User reorders catalog display order to [Google, Slack, GitHub]
        string[] customOrder = [gdriveKey, slackKey, ghKey];
        await requestsNeuron.Reorder(customOrder);

        var catalogReordered = await catalogNeuron.Read();
        Assert.Equal(4L, catalogReordered.Revision);
        Assert.Equal(customOrder, catalogReordered.Order);

        var summaries = await requestsNeuron.ListConnections();
        Assert.Equal(3, summaries.Length);
        Assert.Equal("google", summaries[0].Kind);
        Assert.Equal("slack", summaries[1].Kind);
        Assert.Equal("github", summaries[2].Kind);

        // 3. External webhook reports Slack failing (rate limited / auth error)
        await WebhookSimulator.SimulateFailing(os, brain, "slack", "s_1", detail: "429 Too Many Requests");

        var catalogAfterFailure = await catalogNeuron.Read();
        Assert.Equal(5L, catalogAfterFailure.Revision);
        Assert.Equal(ConnectionStatus.Failing, catalogAfterFailure.RowOf(slackKey)?.Status);

        // Custom display order remains invariant despite row status mutation
        Assert.Equal(customOrder, catalogAfterFailure.Order);
        var summariesAfterFailure = await requestsNeuron.ListConnections();
        Assert.Equal("google", summariesAfterFailure[0].Kind);
        Assert.Equal("slack", summariesAfterFailure[1].Kind);
        Assert.Equal("github", summariesAfterFailure[2].Kind);
        Assert.Equal("failing", summariesAfterFailure[1].Status);

        // 4. User modifies display order again to [GitHub, Google, Slack]
        string[] newOrder = [ghKey, gdriveKey, slackKey];
        await requestsNeuron.Reorder(newOrder);

        var finalCatalog = await catalogNeuron.Read();
        Assert.Equal(6L, finalCatalog.Revision);
        Assert.Equal(newOrder, finalCatalog.Order);
    }

    [Fact]
    public async Task T4_IdentityConnection_WebhookBurstDeduplication_AndIdempotentDeliveryResilience()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        WebhookSimulator.Register(os);

        var brainId = "dedup-resilience";
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));
        await catalogNeuron.Read();

        var key = ConnectionKeys.For(brainId, "github", "burst_conn");
        var t1 = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

        // Initial delivery
        await WebhookSimulator.SimulateConnected(os, brain, "github", "burst_conn", "oauth", "octocat", probedAt: t1);
        var s1 = await catalogNeuron.Read();
        Assert.Equal(1L, s1.Revision);
        Assert.Single(s1.Rows);

        // Webhook provider retries identical payload 5 times in a burst
        for (var i = 0; i < 5; i++)
        {
            await WebhookSimulator.SimulateConnected(os, brain, "github", "burst_conn", "oauth", "octocat", probedAt: t1);
        }

        var s2 = await catalogNeuron.Read();
        // Revision must NOT advance, state must remain identical
        Assert.Equal(1L, s2.Revision);
        Assert.Single(s2.Rows);

        // Webhook delivers Expired fact
        var t2 = t1.AddHours(1);
        await WebhookSimulator.SimulateExpired(os, brain, "github", "burst_conn", probedAt: t2);
        var s3 = await catalogNeuron.Read();
        Assert.Equal(2L, s3.Revision);
        Assert.Equal(ConnectionStatus.Expired, s3.RowOf(key)?.Status);

        // Webhook retries Expired fact 3 times
        for (var i = 0; i < 3; i++)
        {
            await WebhookSimulator.SimulateExpired(os, brain, "github", "burst_conn", probedAt: t2);
        }

        var s4 = await catalogNeuron.Read();
        // Revision remains at 2L, duplicate expired events are dropped cleanly
        Assert.Equal(2L, s4.Revision);
        Assert.Equal(ConnectionStatus.Expired, s4.RowOf(key)?.Status);
    }

    [Fact]
    public async Task T4_IdentityConnection_OnboardingUserJourney_PendingRequestToAdoptionAndActive()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionCatalogNeuron>();
        WebhookSimulator.Register(os);

        var brainId = "onboarding-user";
        var brain = new BrainId(brainId);
        var catalogNeuron = os.Get<IConnectionCatalog>(IConnectionCatalog.Id(brainId));
        var requestsNeuron = os.Get<IConnectionRequests>(IConnectionCatalog.Id(brainId));
        await catalogNeuron.Read();

        // Step 1: User initiates an account connection request for "jira"
        var pendingRef = await requestsNeuron.Request("jira");
        Assert.NotNull(pendingRef);
        Assert.Equal("jira", pendingRef.Kind);
        Assert.False(string.IsNullOrWhiteSpace(pendingRef.Id));

        var stateWithPending = await catalogNeuron.Read();
        Assert.Single(stateWithPending.Pending);
        Assert.Equal(pendingRef.Id, stateWithPending.Pending[0].Id);

        var pendingList = await requestsNeuron.ListPending();
        Assert.Single(pendingList);
        Assert.Equal("jira", pendingList[0].Kind);

        // Step 2: External OAuth adopter handles connection setup
        var connId = pendingRef.Id;
        var connectionKey = ConnectionKeys.For(brainId, "jira", connId);
        await WebhookSimulator.SimulateAdopted(os, brain, "jira", connId, "oauth", "jira-workspace-admin");

        var stateAfterAdopted = await catalogNeuron.Read();
        var adoptedRow = stateAfterAdopted.RowOf(connectionKey);
        Assert.NotNull(adoptedRow);
        Assert.Equal(ConnectionStatus.Disconnected, adoptedRow.Status);
        Assert.Equal("jira-workspace-admin", adoptedRow.Account);

        // Step 3: OAuth credentials verified and connection marked Connected
        await WebhookSimulator.SimulateConnected(os, brain, "jira", connId, "oauth", "jira-workspace-admin");

        var finalState = await catalogNeuron.Read();
        var activeRow = finalState.RowOf(connectionKey);
        Assert.NotNull(activeRow);
        Assert.Equal(ConnectionStatus.Connected, activeRow.Status);
        Assert.True(finalState.Resolved("jira"));
    }
}
