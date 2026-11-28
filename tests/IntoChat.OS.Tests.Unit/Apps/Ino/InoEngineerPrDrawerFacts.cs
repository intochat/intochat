using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoEngineerPrDrawerFacts
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public async Task OpenDrawer_HydratesEngineerPrArtifact_WithTournamentDiffLogs_AndHidesPinAsApp()
    {
        var fixture = new EngineerDrawerTestState();
        var flowId = "local/flow.engineer/run-a5";
        fixture.RunsByFlowId[flowId] = CreateSampleRunSummary(
            flowId,
            "abcdef1234567890abcdef1234567890abcdef12",
            selfReviewPassed: true,
            selfReviewSummary: "passed (0 blocking, 0 warning)");

        await using var os = CreateTestOs(fixture);
        var ino = await os.Install<InoApp>();

        await ino.Request("open-drawer", new InoOpenDrawer(
            ArtifactId: flowId,
            FlowId: flowId,
            Title: "Add Engineer PR Drawer",
            Kind: InoDrawerArtifact.EngineerPrKind));

        Assert.True(ino.App.State.DrawerOpen);
        var artifact = Assert.IsType<InoDrawerArtifact>(ino.App.State.ActiveArtifact);
        Assert.Equal(InoDrawerArtifact.EngineerPrKind, artifact.Kind);
        Assert.Equal(flowId, artifact.FlowId);
        Assert.Equal("abcdef1234567890abcdef1234567890abcdef12", artifact.CandidateSha);
        Assert.True(artifact.HasVerificationReceipt);
        Assert.True(artifact.BuildVerified);
        Assert.True(artifact.LiveVerified);
        Assert.True(artifact.SelfReviewPassed);
        Assert.Equal("passed (0 blocking, 0 warning)", artifact.SelfReviewSummary);
        Assert.Equal("plan-a5", artifact.ConfirmationPlanId);
        Assert.Null(artifact.BaseBranch);
        Assert.Equal(2, artifact.TournamentCandidates.Length);
        Assert.Equal("Minimal Surgical Patch", artifact.TournamentCandidates[0].StrategyLabel);
        Assert.True(artifact.TournamentCandidates[0].IsSelected);

        // Tournament tab (default "preview")
        var tournamentSplit = Assert.IsType<SplitPane>(InoView.ScreenNode(
            ino.App.State,
            VoiceAbilities.None,
            _ => new Spacer(0)));
        var tournamentJson = JsonSerializer.Serialize(tournamentSplit.Trailing, WebJson);
        Assert.Contains("Tournament", tournamentJson, StringComparison.Ordinal);
        Assert.Contains("Diff", tournamentJson, StringComparison.Ordinal);
        Assert.Contains("Logs", tournamentJson, StringComparison.Ordinal);
        Assert.Contains("Candidate Tournament Scoreboard", tournamentJson, StringComparison.Ordinal);
        Assert.Contains("Minimal Surgical Patch", tournamentJson, StringComparison.Ordinal);
        Assert.Contains("Defensive Edge-Case Hardening", tournamentJson, StringComparison.Ordinal);
        Assert.Contains("Self-Review Passed", tournamentJson, StringComparison.Ordinal);
        Assert.Contains("passed (0 blocking, 0 warning)", tournamentJson, StringComparison.Ordinal);
        Assert.Contains("Approve & Open Draft PR", tournamentJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Pin as App", tournamentJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Pin as App to pop out", tournamentJson, StringComparison.Ordinal);

        // Diff tab
        var diffState = ino.App.State.SelectDrawerTab("diff").Next!;
        var diffSplit = Assert.IsType<SplitPane>(InoView.ScreenNode(diffState, VoiceAbilities.None, _ => new Spacer(0)));
        var diffJson = JsonSerializer.Serialize(diffSplit.Trailing, WebJson);
        Assert.Contains("Winning Unified Diff", diffJson, StringComparison.Ordinal);
        Assert.Contains("+++ b/apps/ino/InoView.cs", diffJson, StringComparison.Ordinal);

        // Logs tab
        var logsState = ino.App.State.SelectDrawerTab("logs").Next!;
        var logsSplit = Assert.IsType<SplitPane>(InoView.ScreenNode(logsState, VoiceAbilities.None, _ => new Spacer(0)));
        var logsJson = JsonSerializer.Serialize(logsSplit.Trailing, WebJson);
        Assert.Contains("Roslyn Build & TRX Tests", logsJson, StringComparison.Ordinal);
        Assert.Contains("42 passed, 0 failed", logsJson, StringComparison.Ordinal);
        Assert.Contains("Live Verify (MCP / Playwright / OTel)", logsJson, StringComparison.Ordinal);
        Assert.Contains("mcp/health-probe", logsJson, StringComparison.Ordinal);
        Assert.Contains("playwright/drawer-smoke", logsJson, StringComparison.Ordinal);
        Assert.Contains("span/http.get.health 200 OK", logsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApproveEngineerPr_PrimaryConfirmationGateFlow_ResolvesPlan_AndTransitionsToOpenPrOnGitHub_WithIntentKindCopy()
    {
        var fixture = new EngineerDrawerTestState();
        var flowId = "local/flow.engineer/run-confirm-gate";
        var commitSha = "111122223333444455556666777788889999aaaa";
        var prUrl = "https://github.com/intochat/intochat/pull/777";
        // Simulate EngineerFlow raising its RequiresPerson confirmation plan and executing push -> pr upon direct person approval.
        var brain = new BrainId("local");
        var planId = "plan-confirm-777";
        fixture.RunsByFlowId[flowId] = CreateSampleRunSummary(
            flowId,
            commitSha,
            selfReviewPassed: true,
            selfReviewSummary: "passed (0 blocking, 0 warning)",
            confirmationPlanId: planId,
            pushedRef: null);

        await using var os = CreateTestOs(fixture);
        var ino = await os.Install<InoApp>();

        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));

        var confirmationRequests = os.Get<IConfirmationRequests>(IConfirmationRequests.Of(brain));
        await confirmationRequests.Request(new ConfirmationPlan(
            Id: planId,
            Action: ConfirmationAction.Other,
            Destructive: true,
            Title: "Push & open Draft PR?",
            Body: "Push branch and open Draft PR",
            ApproveLabel: "Push & Open Draft PR",
            DenyLabel: "Cancel",
            TargetLabel: "intochat/intochat")
        {
            RequiresPerson = true,
            Subject = commitSha,
        });

        await ino.Request("open-drawer", new InoOpenDrawer(flowId, flowId, Kind: InoDrawerArtifact.EngineerPrKind));

        // Drawer renders the direct-person ApprovalCardNode targeting the confirmation plan (never relaying Resolve via app).
        var pendingDrawer = InoView.RenderArtifactDrawer(ino.App.State, ino.App.State.ActiveArtifact!);
        var pendingJson = JsonSerializer.Serialize(pendingDrawer, WebJson);
        Assert.Contains(planId, pendingJson, StringComparison.Ordinal);

        // Before the person answers /confirmations/{planId}, ApproveEngineerPr fails closed with confirmation_required.
        await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(flowId, commitSha));
        Assert.Contains("confirmation_required", ino.App.LastRejection!.Rules);
        Assert.Empty(fixture.CreatedPullRequests);

        // Direct person approves /confirmations/{planId}; EngineerFlow completes push -> pr and persists PullRequestUrl.
        fixture.RunsByFlowId[flowId] = CreateSampleRunSummary(
            flowId,
            commitSha,
            selfReviewPassed: true,
            selfReviewSummary: "passed (0 blocking, 0 warning)",
            confirmationPlanId: planId,
            status: FlowStatus.Completed,
            pushedRef: "refs/heads/ino/engineer-run-a5",
            pullRequestUrl: prUrl);

        var inbox = os.Get<IConfirmationInbox>(IConfirmationRequests.Of(brain));
        await inbox.Resolve(planId, approve: true);
        await os.Idle();

        Assert.Empty(fixture.CreatedPullRequests);

        var updated = Assert.IsType<InoDrawerArtifact>(ino.App.State.ActiveArtifact);
        Assert.Equal(prUrl, updated.PullRequestUrl);
        Assert.Null(updated.ApprovalError);
        Assert.Equal("done", updated.Status);

        var drawerNode = InoView.RenderArtifactDrawer(ino.App.State, updated);
        var openPrButton = FindButtons(drawerNode).Single(static b => string.Equals(b.Key, InoView.DrawerOpenPrKey, StringComparison.Ordinal));
        Assert.Equal("Open PR on GitHub", openPrButton.Label);
        Assert.NotNull(openPrButton.Intent);
        Assert.Equal(IntentKind.Copy, openPrButton.Intent!.Kind);
        Assert.Equal(prUrl, openPrButton.Intent.Arg);

        var rendered = JsonSerializer.Serialize(drawerNode, WebJson);
        Assert.DoesNotContain("Approve & Open Draft PR", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Pin as App", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApproveEngineerPr_AsyncConfirmationGate_TransitionsDrawerOnFlowsChanged()
    {
        var fixture = new EngineerDrawerTestState();
        var flowId = "local/flow.engineer/run-async-gate";
        var commitSha = "222233334444555566667777888899990000aaaa";
        var prUrl = "https://github.com/intochat/intochat/pull/888";
        var planId = "plan-async-888";
        fixture.RunsByFlowId[flowId] = CreateSampleRunSummary(
            flowId,
            commitSha,
            selfReviewPassed: true,
            selfReviewSummary: "passed (0 blocking, 0 warning)",
            confirmationPlanId: planId,
            pushedRef: null);

        var brain = new BrainId("local");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
        await using var os = CreateTestOs(fixture);
        var ino = await os.Install<InoApp>();

        var confirmationRequests = os.Get<IConfirmationRequests>(IConfirmationRequests.Of(brain));
        await confirmationRequests.Request(new ConfirmationPlan(
            Id: planId,
            Action: ConfirmationAction.Other,
            Destructive: true,
            Title: "Push & open Draft PR?",
            Body: "Push branch and open Draft PR",
            ApproveLabel: "Push & Open Draft PR",
            DenyLabel: "Cancel",
            TargetLabel: "intochat/intochat")
        {
            RequiresPerson = true,
            Subject = commitSha,
        });
        await os.Get<IConfirmationInbox>(IConfirmationRequests.Of(brain)).Resolve(planId, approve: true);

        await ino.Request("open-drawer", new InoOpenDrawer(flowId, flowId, Kind: InoDrawerArtifact.EngineerPrKind));
        await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(flowId, commitSha));

        Assert.Null(ino.App.LastRejection);
        Assert.Equal("running", ino.App.State.ActiveArtifact?.Status);
        Assert.Null(ino.App.State.ActiveArtifact?.PullRequestUrl);

        // Simulate EngineerFlow finishing push -> pr asynchronously and emitting FlowsChanged.
        fixture.RunsByFlowId[flowId] = CreateSampleRunSummary(
            flowId,
            commitSha,
            selfReviewPassed: true,
            selfReviewSummary: "passed (0 blocking, 0 warning)",
            confirmationPlanId: planId,
            status: FlowStatus.Completed,
            pushedRef: "refs/heads/ino/engineer-run-a5",
            pullRequestUrl: prUrl);

        await ino.App.OnFlowsChanged(new FlowsChanged(flowId));

        Assert.Equal(prUrl, ino.App.State.ActiveArtifact?.PullRequestUrl);
        Assert.Equal("done", ino.App.State.ActiveArtifact?.Status);
    }

    [Fact]
    public async Task ApproveEngineerPr_FallbackPath_RequiresPushedRefAndExplicitBaseBranch_AndNeverHardcodesMain()
    {
        var fixture = new EngineerDrawerTestState();
        var flowId = "local/flow.engineer/run-fallback";
        var commitSha = "333344445555666677778888999900001111aaaa";

        // Case 1: No ConfirmationPlanId AND no PushedRef -> fail closed (branch_not_pushed).
        fixture.RunsByFlowId[flowId] = CreateSampleRunSummary(
            flowId,
            commitSha,
            selfReviewPassed: true,
            selfReviewSummary: "passed",
            confirmationPlanId: null,
            pushedRef: null);

        var brain = new BrainId("local");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
        await using var os = CreateTestOs(fixture);
        var ino = await os.Install<InoApp>();

        await ino.Request("open-drawer", new InoOpenDrawer(flowId, flowId, Kind: InoDrawerArtifact.EngineerPrKind));
        await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(flowId, commitSha, BaseBranch: "master"));
        Assert.Contains("branch_not_pushed", ino.App.LastRejection!.Rules);
        Assert.Empty(fixture.CreatedPullRequests);

        // Case 2: PushedRef present, but BaseBranch omitted -> fail closed (missing_base_branch), NEVER hardcode "main".
        fixture.RunsByFlowId[flowId] = CreateSampleRunSummary(
            flowId,
            commitSha,
            selfReviewPassed: true,
            selfReviewSummary: "passed",
            confirmationPlanId: null,
            pushedRef: "refs/heads/ino/engineer-run-a5");

        await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(flowId, commitSha, BaseBranch: null));
        Assert.Contains("missing_base_branch", ino.App.LastRejection!.Rules);
        Assert.Empty(fixture.CreatedPullRequests);

        // Case 3: PushedRef present AND explicit BaseBranch: "master" -> invokes native IRepository with BaseBranch "master".
        await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(flowId, commitSha, BaseBranch: "master"));
        Assert.Null(ino.App.LastRejection);
        var created = Assert.Single(fixture.CreatedPullRequests);
        Assert.True(created.Draft);
        Assert.Equal("master", created.BaseBranch);
        Assert.Equal("ino/engineer-run-a5", created.HeadBranch);
        Assert.Equal("https://github.com/intochat/intochat/pull/777", ino.App.State.ActiveArtifact?.PullRequestUrl);
    }

    [Fact]
    public async Task ApproveEngineerPr_NullAgentAndForeignBrainCallers_FailClosed_AndMethodCarriesPersonOnlyAttribute()
    {
        var method = typeof(InoApp).GetMethod(nameof(InoApp.ApproveEngineerPr));
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<PersonOnlyAttribute>());

        var fixture = new EngineerDrawerTestState();
        var flowId = "local/flow.engineer/run-auth-guard";
        var commitSha = "111122223333444455556666777788889999bbbb";
        fixture.RunsByFlowId[flowId] = CreateSampleRunSummary(
            flowId,
            commitSha,
            selfReviewPassed: true,
            selfReviewSummary: "passed (0 blocking, 0 warning)");

        var brain = new BrainId("local");
        var foreignBrain = new BrainId("other-brain");
        await using var os = CreateTestOs(fixture);
        var ino = await os.Install<InoApp>();
        var screen = os.Get<IScreen>(new NeuronId(brain, "app.ino", "default"));
        var approveArgs = DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
            new InoApproveEngineerPr(flowId, commitSha),
            typeof(InoApproveEngineerPr));
        var approveCmd = new Command(".", "ino.approve-engineer-pr", approveArgs);

        // 1. Null caller MUST fail closed at screen boundary and inside ApproveEngineerPr.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => screen.Dispatch(approveCmd));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ino.App.ApproveEngineerPr(new InoApproveEngineerPr(flowId, commitSha)));

        // 2. Kernel caller MUST fail closed.
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Kernel(brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => screen.Dispatch(approveCmd));
        }

        // 3. Agent / delegated app caller MUST fail closed.
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.App("local/apps/agent", new Actor.Person("p1", "a1"), brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => screen.Dispatch(approveCmd));
        }

        // 4. Foreign-brain person caller MUST fail closed.
        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("foreign-user", "foreign-user", foreignBrain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => screen.Dispatch(approveCmd));
        }

        Assert.Empty(fixture.ResolvedPlans);
        Assert.Empty(fixture.CreatedPullRequests);
    }

    [Fact]
    public async Task ApproveEngineerPr_MissingExplicitExpectedCommitSha_FailsClosed_WithoutCachedFallback()
    {
        var fixture = new EngineerDrawerTestState();
        var flowId = "local/flow.engineer/run-explicit-sha";
        var commitSha = "444455556666777788889999000011112222aaaa";
        fixture.RunsByFlowId[flowId] = CreateSampleRunSummary(
            flowId,
            commitSha,
            selfReviewPassed: true,
            selfReviewSummary: "passed");

        var brain = new BrainId("local");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
        await using var os = CreateTestOs(fixture);
        var ino = await os.Install<InoApp>();

        await ino.Request("open-drawer", new InoOpenDrawer(flowId, flowId, Kind: InoDrawerArtifact.EngineerPrKind));
        Assert.Equal(commitSha, ino.App.State.ActiveArtifact?.CandidateSha);

        // Passing null/whitespace ExpectedCommitSha MUST fail closed rather than falling back to cached drawer SHA.
        await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(flowId, ExpectedCommitSha: "   "));
        Assert.NotNull(ino.App.LastRejection);
        Assert.Contains("missing_commit_sha", ino.App.LastRejection!.Rules);
        Assert.Empty(fixture.ResolvedPlans);
        Assert.Empty(fixture.CreatedPullRequests);
    }

    [Fact]
    public async Task ApproveEngineerPr_AndCanApprove_FailClosed_ForStatusReceiptBuildLiveSelfReviewAndShaDrift()
    {
        var brain = new BrainId("local");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));

        // 1. Non-Waiting status
        await AssertGateBlockedAsync("local/flow.engineer/gate-status", "flow_not_waiting",
            (fid, sha) => CreateSampleRunSummary(fid, sha, selfReviewPassed: true, selfReviewSummary: "passed", status: FlowStatus.Running));

        // 2. Missing Receipt
        await AssertGateBlockedAsync("local/flow.engineer/gate-receipt", "missing_receipt",
            (fid, sha) => CreateSampleRunSummary(fid, sha, selfReviewPassed: true, selfReviewSummary: "passed", omitReceipt: true));

        // 3. BuildVerified == false
        await AssertGateBlockedAsync("local/flow.engineer/gate-build", "build_not_verified",
            (fid, sha) => CreateSampleRunSummary(fid, sha, selfReviewPassed: true, selfReviewSummary: "passed", buildVerified: false));

        // 4. LiveVerified == false
        await AssertGateBlockedAsync("local/flow.engineer/gate-live", "live_not_verified",
            (fid, sha) => CreateSampleRunSummary(fid, sha, selfReviewPassed: true, selfReviewSummary: "passed", liveVerified: false));

        // 5. SelfReviewPassed == false
        await AssertGateBlockedAsync("local/flow.engineer/gate-self-review", "self_review_failed",
            (fid, sha) => CreateSampleRunSummary(fid, sha, selfReviewPassed: false, selfReviewSummary: "blocked (1 blocking)"));

        // 6. SHA drift between review and approval
        var driftFixture = new EngineerDrawerTestState();
        var driftFlowId = "local/flow.engineer/gate-sha-drift";
        var reviewedSha = "aaaa111122223333444455556666777788889999";
        var mutatedSha = "bbbb999988887777666655554444333322221111";
        driftFixture.RunsByFlowId[driftFlowId] = CreateSampleRunSummary(driftFlowId, reviewedSha, selfReviewPassed: true, selfReviewSummary: "passed");

        await using var driftOs = CreateTestOs(driftFixture);
        var driftIno = await driftOs.Install<InoApp>();
        await driftIno.Request("open-drawer", new InoOpenDrawer(driftFlowId, driftFlowId, Kind: InoDrawerArtifact.EngineerPrKind));

        driftFixture.RunsByFlowId[driftFlowId] = CreateSampleRunSummary(driftFlowId, mutatedSha, selfReviewPassed: true, selfReviewSummary: "passed");
        await driftIno.Request("approve-engineer-pr", new InoApproveEngineerPr(driftFlowId, reviewedSha));

        Assert.Empty(driftFixture.ResolvedPlans);
        Assert.Empty(driftFixture.CreatedPullRequests);
        Assert.Contains("sha_mismatch", driftIno.App.LastRejection!.Rules);
        Assert.Equal(mutatedSha, driftIno.App.State.ActiveArtifact?.CandidateSha);
        Assert.Contains("Commit SHA mismatch", driftIno.App.State.ActiveArtifact?.ApprovalError, StringComparison.Ordinal);
    }

    private static async Task AssertGateBlockedAsync(
        string flowId,
        string expectedRule,
        Func<string, string, EngineerRunSummary> buildSummary)
    {
        var fixture = new EngineerDrawerTestState();
        var commitSha = "cccc111122223333444455556666777788889999";
        fixture.RunsByFlowId[flowId] = buildSummary(flowId, commitSha);

        await using var os = CreateTestOs(fixture);
        var ino = await os.Install<InoApp>();

        await ino.Request("open-drawer", new InoOpenDrawer(flowId, flowId, Kind: InoDrawerArtifact.EngineerPrKind));
        var artifact = Assert.IsType<InoDrawerArtifact>(ino.App.State.ActiveArtifact);
        var drawerNode = InoView.RenderArtifactDrawer(ino.App.State, artifact);
        var approveButton = FindButtons(drawerNode).Single(static b => string.Equals(b.Key, InoView.DrawerApprovePrKey, StringComparison.Ordinal));
        Assert.False(approveButton.Enabled, $"Expected Approve button disabled for {expectedRule}");

        await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(flowId, commitSha));
        Assert.Empty(fixture.ResolvedPlans);
        Assert.Empty(fixture.CreatedPullRequests);
        Assert.NotNull(ino.App.LastRejection);
        Assert.Contains(expectedRule, ino.App.LastRejection!.Rules);
    }

    [Fact]
    public async Task FallbackRepositoryResolution_RejectsBlankMismatchedAndAmbiguousRepos_FailClosed()
    {
        var brain = new BrainId("local");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
        var commitSha = "555566667777888899990000111122223333aaaa";

        // 1. Blank repoView.Repository -> fail closed
        var blankFixture = new EngineerDrawerTestState { DefaultRepositorySlug = "" };
        var blankFlow = "local/flow.engineer/repo-blank";
        blankFixture.RunsByFlowId[blankFlow] = CreateSampleRunSummary(
            blankFlow, commitSha, selfReviewPassed: true, selfReviewSummary: "passed",
            confirmationPlanId: null, pushedRef: "refs/heads/ino/engineer-run-a5", repoSlug: "intochat/intochat");
        await using (var os = CreateTestOs(blankFixture))
        {
            var ino = await os.Install<InoApp>();
            await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(blankFlow, commitSha, BaseBranch: "master"));
            Assert.Contains("repository_unavailable", ino.App.LastRejection!.Rules);
            Assert.Empty(blankFixture.CreatedPullRequests);
        }

        // 2. Mismatched owner/repo -> fail closed
        var mismatchFixture = new EngineerDrawerTestState { DefaultRepositorySlug = "otherorg/otherrepo" };
        var mismatchFlow = "local/flow.engineer/repo-mismatch";
        mismatchFixture.RunsByFlowId[mismatchFlow] = CreateSampleRunSummary(
            mismatchFlow, commitSha, selfReviewPassed: true, selfReviewSummary: "passed",
            confirmationPlanId: null, pushedRef: "refs/heads/ino/engineer-run-a5", repoSlug: "intochat/intochat");
        await using (var os = CreateTestOs(mismatchFixture))
        {
            var ino = await os.Install<InoApp>();
            await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(mismatchFlow, commitSha, BaseBranch: "master"));
            Assert.Contains("repository_unavailable", ino.App.LastRejection!.Rules);
            Assert.Empty(mismatchFixture.CreatedPullRequests);
        }

        // 3. Ambiguous short slug matching two distinct connected GitHub repos -> fail closed
        var ambiguousFixture = new EngineerDrawerTestState
        {
            DefaultRepositorySlug = "org-a/os",
            CatalogConnectionIds = ["conn-a", "conn-b"],
            RepositoryByKey =
            {
                ["connection/github/conn-a"] = ("conn-a", true, "org-a/os"),
                ["connection/github/conn-b"] = ("conn-b", true, "org-b/os"),
            },
        };
        var ambFlow = "local/flow.engineer/repo-ambiguous";
        ambiguousFixture.RunsByFlowId[ambFlow] = CreateSampleRunSummary(
            ambFlow, commitSha, selfReviewPassed: true, selfReviewSummary: "passed",
            confirmationPlanId: null, pushedRef: "refs/heads/ino/engineer-run-a5", repoSlug: "os");
        await using (var os = CreateTestOs(ambiguousFixture))
        {
            var ino = await os.Install<InoApp>();
            await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(ambFlow, commitSha, BaseBranch: "master"));
            Assert.Contains("repository_unavailable", ino.App.LastRejection!.Rules);
            Assert.Empty(ambiguousFixture.CreatedPullRequests);
        }
    }

    [Theory]
    [InlineData("http://github.com/intochat/intochat/pull/777")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/intochat/intochat/pull/777")]
    [InlineData("not a url")]
    [InlineData("https://")]
    [InlineData("https://github.com/intochat/intochat/pull/777\nX-Injected: true")]
    [InlineData("https://user:pass@github.com/intochat/intochat/pull/777")]
    public void PullRequestUrlValidation_RejectsNonHttpsAndMalformedUrls_InHelperAndDrawer(string unsafeUrl)
    {
        Assert.False(InoCards.TryNormalizeHttpsPullRequestUrl(unsafeUrl, out _));

        var artifact = new InoDrawerArtifact(
            Id: "engineer-pr:run-unsafe",
            Title: "Unsafe URL test",
            Kind: InoDrawerArtifact.EngineerPrKind,
            Status: "waiting",
            FlowId: "local/flow.engineer/run-unsafe",
            CandidateSha: "abcdef1234567890abcdef1234567890abcdef12",
            PullRequestUrl: unsafeUrl,
            SelfReviewPassed: true,
            SelfReviewSummary: "passed",
            BuildVerified: true,
            LiveVerified: true,
            HasVerificationReceipt: true);

        var drawerNode = InoView.RenderArtifactDrawer(new InoAppState().OpenArtifactDrawer(artifact), artifact);
        var buttons = FindButtons(drawerNode);
        Assert.DoesNotContain(buttons, static b => string.Equals(b.Key, InoView.DrawerOpenPrKey, StringComparison.Ordinal));
        Assert.Contains(buttons, static b => string.Equals(b.Key, InoView.DrawerApprovePrKey, StringComparison.Ordinal));
        Assert.DoesNotContain(buttons, b => string.Equals(b.Intent?.Arg, unsafeUrl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApproveEngineerPr_RejectsNonHttpsUrlReturnedByRepository_FailClosed()
    {
        var fixture = new EngineerDrawerTestState { ReturnedPullRequestUrl = "javascript:alert(1)" };
        var flowId = "local/flow.engineer/run-bad-pr-url";
        var commitSha = "111122223333444455556666777788889999cccc";
        fixture.RunsByFlowId[flowId] = CreateSampleRunSummary(
            flowId,
            commitSha,
            selfReviewPassed: true,
            selfReviewSummary: "passed (0 blocking, 0 warning)",
            confirmationPlanId: null,
            pushedRef: "refs/heads/ino/engineer-run-a5");

        var brain = new BrainId("local");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
        await using var os = CreateTestOs(fixture);
        var ino = await os.Install<InoApp>();

        await ino.Request("open-drawer", new InoOpenDrawer(flowId, flowId, Kind: InoDrawerArtifact.EngineerPrKind));
        await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(flowId, commitSha, BaseBranch: "master"));

        Assert.NotNull(ino.App.LastRejection);
        Assert.Contains("invalid_pr_url", ino.App.LastRejection!.Rules);
        Assert.Null(ino.App.State.ActiveArtifact?.PullRequestUrl);
        Assert.Contains("invalid or non-HTTPS", ino.App.State.ActiveArtifact?.ApprovalError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOrUnknownFlowId_RejectsCleanly_WithoutUnhandledExceptions()
    {
        var fixture = new EngineerDrawerTestState();
        var brain = new BrainId("local");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
        await using var os = CreateTestOs(fixture);
        var ino = await os.Install<InoApp>();

        await ino.Request("open-drawer", new InoOpenDrawer(FlowId: "  ", Kind: InoDrawerArtifact.EngineerPrKind));
        Assert.NotNull(ino.App.LastRejection);
        Assert.Contains("invalid_engineer_flow", ino.App.LastRejection!.Rules);
        Assert.False(ino.App.State.DrawerOpen);

        await ino.Request("open-drawer", new InoOpenDrawer(FlowId: "local/flow.engineer/missing-999", Kind: InoDrawerArtifact.EngineerPrKind));
        Assert.NotNull(ino.App.LastRejection);
        Assert.Contains("engineer_run_not_found", ino.App.LastRejection!.Rules);
        Assert.False(ino.App.State.DrawerOpen);

        await ino.Request("approve-engineer-pr", new InoApproveEngineerPr(FlowId: null, ExpectedCommitSha: "deadbeef"));
        Assert.NotNull(ino.App.LastRejection);
        Assert.Contains("invalid_engineer_flow", ino.App.LastRejection!.Rules);
        Assert.Empty(fixture.CreatedPullRequests);
    }

    [Fact]
    public async Task PassiveReads_NeverMutateInoAppState()
    {
        var fixture = new EngineerDrawerTestState();
        var brain = new BrainId("local");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
        await using var os = CreateTestOs(fixture);
        var ino = await os.Install<InoApp>();
        var screen = os.Get<IScreen>(new NeuronId(brain, "app.ino", "default"));

        var before = ino.App.State;
        _ = await screen.Open();
        _ = await screen.Read();
        _ = await screen.ReadChild(InoView.ConversationKey);
        var after = ino.App.State;

        Assert.Equal(before, after);
        Assert.False(after.DrawerOpen);
        Assert.Null(after.ActiveArtifact);
    }

    [Fact]
    public void AotSourceGeneratedJson_RoundTripsEngineerPrDrawerState_AndHandlesDiffFallbackAndTruncation()
    {
        var largeDiff = "+" + new string('x', InoCards.MaxDisplayedDiffChars + 512);
        var truncatedSummary = CreateSampleRunSummary(
            "local/flow.engineer/run-trunc",
            "dddd111122223333444455556666777788889999",
            selfReviewPassed: true,
            selfReviewSummary: "passed",
            unifiedDiff: largeDiff);
        var truncatedArtifact = InoCards.FromEngineerRunSummary(truncatedSummary);
        Assert.NotNull(truncatedArtifact.DiffText);
        Assert.Contains("diff truncated for display", truncatedArtifact.DiffText, StringComparison.Ordinal);

        var emptyDiffSummary = CreateSampleRunSummary(
            "local/flow.engineer/run-empty-diff",
            "eeee111122223333444455556666777788889999",
            selfReviewPassed: true,
            selfReviewSummary: "passed",
            unifiedDiff: "");
        var emptyDiffArtifact = InoCards.FromEngineerRunSummary(emptyDiffSummary);
        Assert.Null(emptyDiffArtifact.DiffText);
        var diffTabJson = JsonSerializer.Serialize(
            InoView.RenderArtifactDrawer(new InoAppState().OpenArtifactDrawer(emptyDiffArtifact).SelectDrawerTab("diff").Next!, emptyDiffArtifact),
            WebJson);
        Assert.Contains("No unified diff available yet.", diffTabJson, StringComparison.Ordinal);

        // Source-generated AOT JSON round-trip (zero reflection fallback)
        var state = new InoAppState().OpenArtifactDrawer(truncatedArtifact);
        var artifactJson = JsonSerializer.Serialize(truncatedArtifact, InoJson.Default.InoDrawerArtifact);
        var roundTrippedArtifact = JsonSerializer.Deserialize(artifactJson, InoJson.Default.InoDrawerArtifact);
        Assert.Equal(truncatedArtifact, roundTrippedArtifact);
        Assert.Equal(truncatedArtifact.GetHashCode(), roundTrippedArtifact!.GetHashCode());

        var stateJson = JsonSerializer.Serialize(state, InoJson.Default.InoAppState);
        var roundTrippedState = JsonSerializer.Deserialize(stateJson, InoJson.Default.InoAppState);
        Assert.NotNull(roundTrippedState);
        Assert.Equal(truncatedArtifact, roundTrippedState!.ActiveArtifact);

        // Tournament alias maps cleanly to "preview" while preserving ValidDrawerTabInvariant.
        var tournamentTab = state.SelectDrawerTab("tournament");
        Assert.True(tournamentTab.IsAccepted);
        Assert.Equal("preview", tournamentTab.Next!.DrawerTab);
        Assert.True(tournamentTab.Next.ValidDrawerTabInvariant);
    }

    private static TestOS CreateTestOs(EngineerDrawerTestState fixture)
    {
        var services = new ServiceCollection()
            .AddSingleton(fixture)
            .BuildServiceProvider();
        var os = TestOS.Local(o => o.Services = services);
        os.Register<FakeEngineerRunsNeuron>();
        os.Register<FakeGitHubRepositoryNeuron>();
        os.Register<FakeConnectionCatalogNeuron>();
        os.Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        return os;
    }

    private static List<Button> FindButtons(Node node)
    {
        var result = new List<Button>();
        Collect(node, result);
        return result;

        static void Collect(Node? current, List<Button> acc)
        {
            switch (current)
            {
                case null:
                    return;
                case Button button:
                    acc.Add(button);
                    return;
                case Surface surface:
                    foreach (var child in surface.Children) { Collect(child, acc); }
                    return;
                case Column column:
                    foreach (var child in column.Children) { Collect(child, acc); }
                    return;
                case Row row:
                    foreach (var child in row.Children) { Collect(child, acc); }
                    return;
                case Card card:
                    foreach (var child in card.Children) { Collect(child, acc); }
                    return;
                case SplitPane split:
                    Collect(split.Leading, acc);
                    Collect(split.Trailing, acc);
                    return;
            }
        }
    }

    private static EngineerRunSummary CreateSampleRunSummary(
        string flowId,
        string commitSha,
        bool selfReviewPassed,
        string selfReviewSummary,
        string? unifiedDiff = null,
        string? confirmationPlanId = "plan-a5",
        FlowStatus status = FlowStatus.Waiting,
        string? pushedRef = "refs/heads/ino/engineer-run-a5",
        string? pullRequestUrl = null,
        bool buildVerified = true,
        bool liveVerified = true,
        bool omitReceipt = false,
        string repoSlug = "intochat/intochat")
    {
        var diff = unifiedDiff ?? "diff --git a/apps/ino/InoView.cs b/apps/ino/InoView.cs\n+++ b/apps/ino/InoView.cs\n@@ -1,2 +1,3 @@\n+public const string DrawerApprovePrKey = \"ino:drawer-approve-pr\";";
        var selfReviewReport = selfReviewPassed
            ? SelfReviewReport.Empty
            : new SelfReviewReport([new ReviewFinding("INT001", ReviewFindingSource.Analyzer, "apps/ino/InoApp.cs", 42, ReviewSeverity.Blocking, "Blocking analyzer error")]);

        var buildReport = new EngineerBuildReport(
            Succeeded: buildVerified,
            DotnetBuildPassed: buildVerified,
            FlutterAnalyzePassed: buildVerified,
            DotnetTestPassed: buildVerified,
            PassedTests: 42,
            FailedTests: buildVerified ? 0 : 1,
            WarningCount: 0,
            ErrorCount: buildVerified ? 0 : 1,
            Diagnostics: ["Roslyn: 0 errors, 0 warnings", "TRX: 42 passed, 0 failed"],
            DurationMs: 1250,
            Summary: "Build and TRX tests green.");

        var liveReport = new EngineerLiveReport(
            Succeeded: liveVerified,
            HostHealthy: liveVerified,
            McpProbesPassed: liveVerified,
            PlaywrightPassed: liveVerified,
            Http4xxCount: 0,
            Http5xxCount: 0,
            ErrorSpanCount: 0,
            ErrorLogCount: 0,
            Probes:
            [
                new EngineerLiveProbe("health-probe", "mcp", liveVerified, 200, "MCP tool catalog healthy"),
                new EngineerLiveProbe("drawer-smoke", "playwright", liveVerified, 200, "Drawer tabs rendered cleanly"),
            ],
            TraceSummaries: ["span/http.get.health 200 OK (12ms)"],
            Summary: "All live verification probes passed.");

        var winnerScore = new EngineerCandidateScore(
            BuildPassed: buildVerified,
            TestsPassed: buildVerified,
            LiveVerifyPassed: liveVerified,
            ZeroTelemetryErrors: true,
            ChangedLines: 14,
            TotalScore: 987,
            CommitSha: commitSha,
            UnifiedDiff: diff,
            Summary: "Score=987 · Build=PASS, Tests=PASS, Live=PASS, OTel=0 err");

        var runnerUpScore = new EngineerCandidateScore(
            BuildPassed: true,
            TestsPassed: true,
            LiveVerifyPassed: false,
            ZeroTelemetryErrors: true,
            ChangedLines: 38,
            TotalScore: 612,
            CommitSha: "9999888877776666555544443333222211110000",
            UnifiedDiff: "+runner-up",
            Summary: "Score=612 · Build=PASS, Tests=PASS, Live=FAIL");

        var candidates = new EngineerCandidateResult[]
        {
            new(
                InstanceIndex: 0,
                Strategy: "surgical-patch",
                StrategyLabel: "Minimal Surgical Patch",
                LeaseId: "lease-0",
                Backend: "LocalGit",
                BranchName: "ino/engineer-run-a5",
                Status: "verified",
                CompletedTaskIds: ["t-1"],
                ChangedFiles: ["apps/ino/InoView.cs"],
                Rank: 1,
                IsSelected: true,
                Score: winnerScore,
                Build: buildReport,
                Live: liveReport,
                SelfReview: selfReviewReport),
            new(
                InstanceIndex: 1,
                Strategy: "defensive-hardening",
                StrategyLabel: "Defensive Edge-Case Hardening",
                LeaseId: "lease-1",
                Backend: "LocalGit",
                BranchName: "ino/engineer-run-a5-alt",
                Status: "completed",
                CompletedTaskIds: ["t-1"],
                ChangedFiles: ["apps/ino/InoView.cs"],
                Rank: 2,
                IsSelected: false,
                Score: runnerUpScore,
                SelfReview: SelfReviewReport.Empty),
        };

        EngineerVerificationReceipt? receipt = omitReceipt
            ? null
            : new EngineerVerificationReceipt(
                CommitSha: commitSha,
                PullRequestUrl: pullRequestUrl,
                PushedRef: pushedRef,
                BuildVerified: buildVerified,
                LiveVerified: liveVerified,
                CandidateHostKind: "LocalSlot",
                VerificationSummary: "Roslyn + TRX + MCP + Playwright + OTel verified.",
                SelfReviewPassed: selfReviewPassed,
                SelfReviewSummary: selfReviewSummary);

        return new EngineerRunSummary(
            Ok: true,
            FlowId: flowId,
            RunId: "run-a5",
            RepoSlug: repoSlug,
            Goal: "Add Ino Engineer PR Drawer with Tournament, Diff, and Logs tabs",
            Backend: "LocalGit",
            ParallelInstances: 2,
            Status: status,
            CurrentStage: status == FlowStatus.Completed ? "done" : "approve",
            CompletedStages: ["plan", "edit", "build", "live-verify", "self-review"],
            ConfirmationPlanId: confirmationPlanId,
            TraceId: "trace-a5",
            WinningCommitSha: commitSha,
            PushedRef: pushedRef,
            PullRequestUrl: pullRequestUrl,
            EngineerAppUri: "os://window/open?app=ino",
            TasksBoardUri: "os://window/open?app=tasks",
            Plan: null,
            Instances: [],
            DiagnosticsAndTraces: ["otel: 0 error spans across 18 trace events"],
            SummaryText: "Candidate #1 won tournament with score 987.",
            SelectedCandidateIndex: 0,
            Candidates: candidates,
            Receipt: receipt,
            SelfReview: selfReviewReport);
    }

    private sealed class EngineerDrawerTestState
    {
        public Dictionary<string, EngineerRunSummary> RunsByFlowId { get; } = new(StringComparer.Ordinal);
        public List<(string PlanId, bool Approve)> ResolvedPlans { get; } = [];
        public Action<string, bool>? OnPlanResolved { get; set; }
        public List<CreatePullRequest> CreatedPullRequests { get; } = [];
        public bool DefaultConnected { get; set; } = true;
        public string DefaultRepositorySlug { get; set; } = "intochat/intochat";
        public string ReturnedPullRequestUrl { get; set; } = "https://github.com/intochat/intochat/pull/777";
        public string[] CatalogConnectionIds { get; set; } = [];
        public Dictionary<string, (string ConnectionId, bool Connected, string Repository)> RepositoryByKey { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    [Neuron("engineer.runs")]
    private sealed class FakeEngineerRunsNeuron(EngineerDrawerTestState state) : Neuron, IEngineerRuns
    {
        public Task<EngineerRunSummary> StartEngineerRun(EngineerDelegationRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<EngineerRunSummary> InspectEngineerRun(string? flowId = null, CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(flowId) && state.RunsByFlowId.TryGetValue(flowId.Trim(), out var found))
            {
                return Task.FromResult(found);
            }

            return Task.FromResult(new EngineerRunSummary(
                Ok: false,
                FlowId: flowId ?? string.Empty,
                RunId: string.Empty,
                RepoSlug: "os",
                Goal: string.Empty,
                Backend: "LocalGit",
                ParallelInstances: 1,
                Status: FlowStatus.Failed,
                CurrentStage: "none",
                CompletedStages: [],
                ConfirmationPlanId: null,
                TraceId: null,
                WinningCommitSha: null,
                PushedRef: null,
                PullRequestUrl: null,
                EngineerAppUri: string.Empty,
                TasksBoardUri: string.Empty,
                Plan: null,
                Instances: [],
                DiagnosticsAndTraces: [],
                SummaryText: $"Engineer run '{flowId}' was not found."));
        }

        public Task<EngineerRunSummary> SelectEngineerCandidate(string flowId, int candidateIndex, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<EngineerRunSummary> RatifyEngineerPlan(
            string flowId,
            string[]? approvedCleanupTargets = null,
            EngineerTaskSlice[]? slices = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Neuron(IConnectionCatalog.TypeName)]
    private sealed class FakeConnectionCatalogNeuron(EngineerDrawerTestState state) : Neuron, IConnectionCatalog
    {
        public Task Subscribed() => Task.CompletedTask;

        public Task<ConnectionCatalog> Read()
        {
            var rows = state.CatalogConnectionIds
                .Select(static id => new CatalogRow(
                    $"connection/github/{id}",
                    "github",
                    id,
                    DigitalBrain.Capabilities.Connections.ConnectionStatus.Connected,
                    id,
                    DateTimeOffset.UnixEpoch))
                .ToArray();
            return Task.FromResult(new ConnectionCatalog { Rows = rows });
        }
    }

    [Neuron("github")]
    private sealed class FakeGitHubRepositoryNeuron(EngineerDrawerTestState state) : Neuron, IRepository
    {
        public Task<RepositoryView> Refresh(RefreshRepository request) => Read();

        public Task<RepositoryView> Read()
        {
            if (state.RepositoryByKey.TryGetValue(Id.Key, out var configured))
            {
                return Task.FromResult(new RepositoryView(configured.ConnectionId, configured.Connected, [], DateTimeOffset.UnixEpoch, configured.Repository));
            }

            return Task.FromResult(new RepositoryView(Id.Key, state.DefaultConnected, [], DateTimeOffset.UnixEpoch, state.DefaultRepositorySlug));
        }

        public Task<PullRequestRead> ReadPullRequest(ReadPullRequest query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PullRequestsRead> ReadPullRequests() => throw new NotSupportedException();

        public Task<ReviewEvidenceRead> ReadReviewEvidence(ReadReviewEvidence query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RequiredChecksRead> ReadRequiredChecks(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task PushBranch(PushBranchRequest request) => Task.CompletedTask;

        public Task<PullRequestView> CreatePullRequest(CreatePullRequest request)
        {
            state.CreatedPullRequests.Add(request);
            return Task.FromResult(new PullRequestView(
                Number: 777,
                Title: request.Title,
                Url: state.ReturnedPullRequestUrl,
                HeadSha: "111122223333444455556666777788889999aaaa",
                BaseSha: "0000000000000000000000000000000000000000",
                IsOpen: true,
                IsDraft: request.Draft));
        }

        public Task ReplyToReviewComment(ReplyReviewComment request) => throw new NotSupportedException();

        public Task MergePullRequest(MergePullRequest request) => throw new NotSupportedException();
    }
}
