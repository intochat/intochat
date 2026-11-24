using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Tests.Integration.Modules.Roslyn;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Tests.Support.Fakes.GitHub;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public sealed class PrReviewFlowFacts
{
    private const string WebhookSecret = "test-pr-review-flow-secret-0123456789";

    [Fact]
    public void PrReviewTrigger_DeduplicatesSameHeadShaAndIgnoresDrafts()
    {
        var sha1 = new string('1', 40);
        var sha2 = new string('2', 40);
        var seen = new SeenPrHeads();

        // 1. Opened emits ReviewRequested and records subject@sha1.
        var opened = new PullRequestChanged(
            Number: 42,
            Version: "v1",
            SubjectKey: "intochat/os#42",
            Kind: PullRequestKind.Opened,
            HeadSha: sha1,
            Author: "alice",
            IsDraft: false);

        var r1 = PrReviewTrigger.OnChanged(opened, seen);
        var emitted1 = Assert.IsType<ReviewRequested>(Assert.Single(r1.Signals));
        Assert.Equal("intochat/os#42", emitted1.Repository);
        Assert.Equal(42, emitted1.Number);
        Assert.Equal(sha1, emitted1.HeadSha);
        Assert.Contains($"intochat/os#42@{sha1}", r1.Next.Keys);

        // 2. Duplicate HeadSha emits nothing.
        var duplicate = opened with { Version = "v2", Kind = PullRequestKind.Updated };
        var r2 = PrReviewTrigger.OnChanged(duplicate, r1.Next);
        Assert.Empty(r2.Signals);
        Assert.Equal(r1.Next.Keys, r2.Next.Keys);

        // 3. Draft PR with new HeadSha emits nothing and does not record sha2.
        var draft = opened with { Version = "v3", Kind = PullRequestKind.Updated, HeadSha = sha2, IsDraft = true };
        var r3 = PrReviewTrigger.OnChanged(draft, r2.Next);
        Assert.Empty(r3.Signals);
        Assert.DoesNotContain($"intochat/os#42@{sha2}", r3.Next.Keys);

        // 4. Non-reviewable kind (Closed / ChecksChanged) emits nothing.
        var closed = opened with { Version = "v4", Kind = PullRequestKind.Closed, HeadSha = sha2, IsDraft = false };
        var r4 = PrReviewTrigger.OnChanged(closed, r3.Next);
        Assert.Empty(r4.Signals);

        // 5. New HeadSha on non-draft Updated emits ReviewRequested.
        var updated = opened with { Version = "v5", Kind = PullRequestKind.Updated, HeadSha = sha2, IsDraft = false };
        var r5 = PrReviewTrigger.OnChanged(updated, r4.Next);
        var emitted5 = Assert.IsType<ReviewRequested>(Assert.Single(r5.Signals));
        Assert.Equal("intochat/os#42", emitted5.Repository);
        Assert.Equal(42, emitted5.Number);
        Assert.Equal(sha2, emitted5.HeadSha);
        Assert.Contains($"intochat/os#42@{sha2}", r5.Next.Keys);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task PrReviewFlow_FindsSwallowAndWallClockDropsUncitedModelFindingsAndSubmitsCommentReviewAfterApproval(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var headSha = new string('b', 40);
        var diff = """
            diff --git a/lib/Sample/ClockService.cs b/lib/Sample/ClockService.cs
            --- a/lib/Sample/ClockService.cs
            +++ b/lib/Sample/ClockService.cs
            @@ -1,3 +1,12 @@
             namespace Sample;
            +public sealed class ClockService
            +{
            +    public DateTime ReadNow()
            +    {
            +        try { return DateTime.Now; }
            +        catch { }
            +        return default;
            +    }
            +}
            """;

        await using var os = await JourneyOS.Start(
            kind,
            [typeof(AppsModule), typeof(GitHubModule), typeof(IntoChat.OS.Modules.Microsoft.Roslyn.RoslynModule)],
            f => f.GitHub(g => g
                .WebhookSecret(WebhookSecret)
                .Repository("intochat/os", repositoryId: 61001, installationId: 9101)
                .PullRequest("intochat/os", 19, headSha, title: "Add clock helper", author: "bob", diff: diff))
                .Configure(services => services.AddRoslynWorkspace()),
            ct);

        var ada = os.Person(os.Runtime.Brain.Value);
        var github = os.Fake<FakeGitHub>();
        var repoId = await github.ConnectAsync(os.Runtime, ada.Brain, "intochat/os", ct: ct);

        var uncitedModelFinding = new ReviewFinding(
            RuleId: "",
            Source: ReviewFindingSource.Model,
            Path: "lib/Sample/ClockService.cs",
            Line: 4,
            Severity: ReviewSeverity.Warning,
            Message: "Consider renaming ReadNow to GetCurrentTimestamp.");

        var citedModelFinding = new ReviewFinding(
            RuleId: "HR-10",
            Source: ReviewFindingSource.Model,
            Path: "lib/Sample/ClockService.cs",
            Line: 2,
            Severity: ReviewSeverity.Info,
            Message: "Ensure domain primitive constructors stay private (HR-10).");

        var initialState = new PrReviewState(
            Repository: "intochat/os",
            Number: 19,
            HeadSha: headSha,
            Findings: [],
            Verdict: PrReviewVerdict.Comment,
            SubmittedReviewId: null,
            AwaitingApproval: false,
            ConfirmationPlanId: null,
            RepositoryNeuronKey: repoId.Key,
            ModelFindings: [uncitedModelFinding, citedModelFinding]);

        var flows = ada.Get<IFlows>(IFlows.Key(ada.Brain.Value));
        var flowId = await flows.Start(
            IEngineerRuns.PrReviewFlow,
            "Review PR #19",
            parentFlowId: null,
            JsonSerializer.Serialize(initialState, EngineerPipelineStages.Json),
            ct);

        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, ct),
            v => v.Status == FlowStatus.Waiting && v.WaitingStep == "approval-gate");

        Assert.False(string.IsNullOrWhiteSpace(waiting.ConfirmationPlanId));
        Assert.Empty(github.SubmittedReviews);

        var starter = ada.Get<IFlowStarter>(FlowTypeFacts.Id(flowId));
        var headBefore = await starter.Head();
        var stateBefore = JsonSerializer.Deserialize<PrReviewState>(headBefore.InnerJson, EngineerPipelineStages.Json)!;
        Assert.True(stateBefore.AwaitingApproval);
        Assert.Contains(stateBefore.Findings, static f => f.RuleId == "HR-12");
        Assert.Contains(stateBefore.Findings, static f => f.RuleId == "HR-Swallow");
        Assert.Contains(stateBefore.Findings, static f => f.RuleId == "HR-10");
        Assert.DoesNotContain(stateBefore.Findings, static f => string.IsNullOrWhiteSpace(f.RuleId));
        Assert.DoesNotContain(stateBefore.Findings, static f => f.Message.Contains("Consider renaming ReadNow", StringComparison.Ordinal));

        await os.Runtime.ApproveAsPerson(waiting.ConfirmationPlanId!, ct);

        var completed = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, ct),
            v => v.Status == FlowStatus.Completed);

        Assert.Equal(PrReviewFlow.Names, completed.Done);

        var submitted = Assert.Single(github.SubmittedReviews);
        Assert.Equal(19, submitted.Number);
        Assert.Equal(headSha, submitted.HeadSha);
        Assert.Equal(ReviewVerdict.Comment, submitted.Verdict);
        Assert.Contains("HR-12", submitted.Body, StringComparison.Ordinal);
        Assert.Contains("HR-Swallow", submitted.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Consider renaming ReadNow", submitted.Body, StringComparison.Ordinal);

        var headAfter = await starter.Head();
        var stateAfter = JsonSerializer.Deserialize<PrReviewState>(headAfter.InnerJson, EngineerPipelineStages.Json)!;
        Assert.False(stateAfter.AwaitingApproval);
        Assert.False(string.IsNullOrWhiteSpace(stateAfter.SubmittedReviewId));
    }

    [Fact]
    public async Task EngineerPipelineStages_PublishesThroughIRepositoryWhenProvided()
    {
        var ct = TestContext.Current.CancellationToken;
        var commitSha = CommitSha.Parse(new string('c', 40));
        var lease = new WorkTreeLease(
            RunId: "run-54",
            RepositoryId: "os",
            SourceRepoPath: Path.Combine(Path.GetTempPath(), "unused-source"),
            TreePath: Path.Combine(Path.GetTempPath(), "unused-worktree"),
            RealGitDirPath: Path.Combine(Path.GetTempPath(), "unused-gitdir"),
            GitPointerSha256Hex: new string('0', 64),
            BaseBranch: "main",
            BaseCommitSha: new string('0', 40),
            BranchName: "ino/feat-repo-publish",
            GitHubRepo: "intochat/os");
        var changeSet = ChangeSet.Create(
            commitSha,
            [RepoPath.Parse("lib/Demo.cs")],
            "+ // demo",
            additions: 1,
            deletions: 0);

        var planId = "app:engineer:run-54:" + commitSha.Value;
        var confirmations = new ApprovedConfirmationRequests(planId, "person-ada");
        var throwingWorkTree = new ThrowingHostPublishWorkTree();
        var recordingRepo = new RecordingRepository();

        var (approvedBy, pushedCommit, pushedRef) = await EngineerPipelineStages.ExecutePushAsync(
            throwingWorkTree,
            confirmations,
            lease,
            changeSet,
            planId,
            ensureCanPublish: (outcome, expectedSha) =>
            {
                Assert.NotNull(outcome);
                Assert.Equal(ConfirmationOutcome.Approved, outcome.Outcome);
                Assert.Equal(commitSha, expectedSha);
                return outcome.Approver?.Id ?? "person-ada";
            },
            ct,
            repository: recordingRepo);

        Assert.Equal("person-ada", approvedBy);
        Assert.Equal(commitSha, pushedCommit);
        Assert.Equal("refs/heads/ino/feat-repo-publish", pushedRef);
        var pushCall = Assert.Single(recordingRepo.Pushes);
        Assert.Equal("ino/feat-repo-publish", pushCall.Branch);
        Assert.Equal(commitSha.Value, pushCall.CommitSha);

        var prUrl = await EngineerPipelineStages.ExecutePullRequestAsync(
            throwingWorkTree,
            lease,
            changeSet,
            approverId: approvedBy,
            planId: planId,
            goal: "Route publish through IRepository",
            summary: "Added IRepository publish path",
            modelId: "test-model",
            engineerContext: null,
            ct,
            repository: recordingRepo);

        Assert.Equal("https://github.com/intochat/os/pull/77", prUrl);
        var prCall = Assert.Single(recordingRepo.PullRequests);
        Assert.Equal("ino/feat-repo-publish", prCall.HeadBranch);
        Assert.Equal("main", prCall.BaseBranch);
        Assert.True(prCall.Draft);
        Assert.Contains("Route publish through IRepository", prCall.Title, StringComparison.Ordinal);
        Assert.Contains(commitSha.Value, prCall.Body, StringComparison.Ordinal);
    }

    private sealed class ApprovedConfirmationRequests(string planId, string approvedBy) : IConfirmationRequests
    {
        public Task<ConfirmationResult?> Request(ConfirmationPlan plan) => Task.FromResult<ConfirmationResult?>(null);
        public Task Withdraw(string id) => Task.CompletedTask;
        public Task<ConfirmationResult?> Outcome(string id)
            => Task.FromResult<ConfirmationResult?>(
                string.Equals(id, planId, StringComparison.Ordinal)
                    ? new ConfirmationResult(id, ConfirmationOutcome.Approved, ConfirmationDenyReason.None, Skipped: false)
                    {
                        Approver = new ConfirmationPrincipal(approvedBy, PrincipalKind.Person),
                    }
                    : null);
    }

    private sealed class ThrowingHostPublishWorkTree : IWorkTree
    {
        public IReadOnlyList<RegisteredRepository> ListRepositories() => throw new NotSupportedException();
        public void RegisterRepository(RegisteredRepository repository) => throw new NotSupportedException();
        public RegisteredRepository ResolveRepository(RepositoryId repositoryId) => throw new NotSupportedException();
        public Task<WorkTreeLease> PrepareAsync(RepositoryId repositoryId, string runId, string goal, BranchName? baseBranch = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChangeSet> VerifyAndCommitAsync(WorkTreeLease lease, string commitMessage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> PushAsync(WorkTreeLease lease, CommitSha approvedSha, CancellationToken ct = default)
            => throw new InvalidOperationException("Host git PushAsync must not be called when IRepository is provided.");
        public Task<string?> CreateDraftPullRequestAsync(WorkTreeLease lease, string prTitle, string prBody, CancellationToken ct = default)
            => throw new InvalidOperationException("Host gh CreateDraftPullRequestAsync must not be called when IRepository is provided.");
        public Task<PublishedPrResult> PublishAsync(WorkTreeLease lease, CommitSha approvedSha, string prTitle, string prBody, CancellationToken ct = default)
            => throw new InvalidOperationException("Host PublishAsync must not be called when IRepository is provided.");
        public Task CleanupAsync(WorkTreeLease lease, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingRepository : IRepository
    {
        public List<PushBranchRequest> Pushes { get; } = [];
        public List<CreatePullRequest> PullRequests { get; } = [];

        public Task<RepositoryView> Refresh(RefreshRepository request) => throw new NotSupportedException();
        public Task<RepositoryView> Read() => throw new NotSupportedException();
        public Task<PullRequestRead> ReadPullRequest(ReadPullRequest query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PullRequestsRead> ReadPullRequests() => throw new NotSupportedException();
        public Task<ReviewEvidenceRead> ReadReviewEvidence(ReadReviewEvidence query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RequiredChecksRead> ReadRequiredChecks(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplyToReviewComment(ReplyReviewComment request) => throw new NotSupportedException();
        public Task MergePullRequest(MergePullRequest request) => throw new NotSupportedException();

        public Task PushBranch(PushBranchRequest request)
        {
            Pushes.Add(request);
            return Task.CompletedTask;
        }

        public Task<PullRequestView> CreatePullRequest(CreatePullRequest request)
        {
            PullRequests.Add(request);
            return Task.FromResult(new PullRequestView(
                Number: 77,
                Title: request.Title,
                Url: "https://github.com/intochat/os/pull/77",
                HeadSha: new string('c', 40),
                BaseSha: new string('0', 40),
                IsOpen: true,
                IsDraft: request.Draft));
        }
    }
}
