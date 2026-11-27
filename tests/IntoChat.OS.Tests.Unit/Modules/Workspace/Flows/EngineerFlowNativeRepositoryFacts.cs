using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Modules.Workspace.Flows;

public sealed class EngineerFlowNativeRepositoryFacts : IDisposable
{
    private readonly string _tempDir;

    public EngineerFlowNativeRepositoryFacts()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "intochat-native-pr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public async Task GitHubRepositorySource_PushBranchWithLocalWorktree_UsesEphemeralExtraHeader_AndRedactsTokenOnFailure()
    {
        const string mintedToken = "ghs_ephemeral_secret_token_9876543210";
        var expectedBasic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"x-access-token:{mintedToken}"));

        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var registration = new GitHubAppRegistration(new FakeGitHubIntegrations("424242", pem));
        using var tokenHandler = new FakeTokenMintHandler(mintedToken, repoId: 777);
        using var tokens = new GitHubInstallationTokens(registration, tokenHandler, TimeProvider.System);

        ProcessStartInfo? capturedPsi = null;
        var shouldFail = false;
        using var source = new GitHubRepositorySource(
            tokens,
            handler: null,
            time: TimeProvider.System,
            gitPushRunner: (psi, _) =>
            {
                capturedPsi = psi;
                if (shouldFail)
                {
                    return Task.FromResult((128, "", $"fatal: auth failed for {mintedToken} header {expectedBasic}"));
                }

                return Task.FromResult((0, "ok", ""));
            });

        var repo = new GitHubRepository("local/connection/github/main", 424242, 9001, 777, "intochat", "intochat", "app");
        var sha = new string('a', 40);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".git"), "gitdir: ./.git-real\n", Ct);

        await source.PushBranchAsync(repo, new PushBranchRequest("ino/feat-cloud-pr", sha, Force: false, LocalWorktreePath: _tempDir), Ct);

        Assert.NotNull(capturedPsi);
        Assert.Equal("git", capturedPsi.FileName);
        Assert.Equal(_tempDir, capturedPsi.WorkingDirectory);

        // Token must travel ONLY in ephemeral GIT_CONFIG_* process environment — NEVER in CLI args or URL.
        Assert.DoesNotContain(capturedPsi.ArgumentList, arg => arg.Contains(mintedToken, StringComparison.Ordinal));
        Assert.DoesNotContain(capturedPsi.ArgumentList, arg => arg.Contains(expectedBasic, StringComparison.Ordinal));
        Assert.Contains("http.sslVerify=true", capturedPsi.ArgumentList);
        Assert.Contains("http.proxy=", capturedPsi.ArgumentList);
        Assert.Contains("http.https://github.com/.proxy=", capturedPsi.ArgumentList);
        Assert.Contains("credential.helper=", capturedPsi.ArgumentList);
        Assert.Contains("core.askPass=", capturedPsi.ArgumentList);
        var separatorIndex = capturedPsi.ArgumentList.IndexOf("--");
        var remoteIndex = capturedPsi.ArgumentList.IndexOf("https://github.com/intochat/intochat.git");
        Assert.True(separatorIndex > 0 && remoteIndex == separatorIndex + 1, "Expected '--' immediately before remoteUrl.");
        Assert.Contains($"{sha}:refs/heads/ino/feat-cloud-pr", capturedPsi.ArgumentList);

        Assert.Equal("1", capturedPsi.Environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("http.https://github.com/.extraheader", capturedPsi.Environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal($"AUTHORIZATION: basic {expectedBasic}", capturedPsi.Environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("/dev/null", capturedPsi.Environment["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("1", capturedPsi.Environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("0", capturedPsi.Environment["GIT_TERMINAL_PROMPT"]);

        // Failure messages must redact both raw token and base64 authorization value.
        shouldFail = true;
        var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() =>
            source.PushBranchAsync(repo, new PushBranchRequest("ino/feat-cloud-pr", sha, Force: false, LocalWorktreePath: _tempDir), Ct));
        Assert.DoesNotContain(mintedToken, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(expectedBasic, ex.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubRepositorySource_RejectsMaliciousGitPushInputsAndNonGitWorktrees()
    {
        var validSha = new string('a', 40);
        var validSha256 = new string('f', 64);
        GitHubRepositorySource.ValidateGitPushInputs("ino/feat-clean", validSha);
        GitHubRepositorySource.ValidateGitPushInputs("ino/feat-sha256", validSha256);

        // Invalid commit SHAs (short, non-hex, flag injection).
        foreach (var badSha in new[] { "", "abc1234", "--force", "+aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('g', 40) })
        {
            Assert.Throws<ArgumentException>(() => GitHubRepositorySource.ValidateGitPushInputs("ino/feat-ok", badSha));
        }

        // Invalid / injection branch names.
        foreach (var badBranch in new[]
        {
            "",
            "-flag",
            "+main",
            ".hidden",
            "/leading",
            "trailing/",
            "bad.lock",
            "a..b",
            "a//b",
            "a@{0}",
            "a:b",
            "a~1",
            "a^1",
            "a?b",
            "a*b",
            "a[b",
            "a\\b",
            "has space",
            "has\nnewline",
        })
        {
            Assert.Throws<ArgumentException>(() => GitHubRepositorySource.ValidateGitPushInputs(badBranch, validSha));
        }

        // Unrooted and non-git LocalWorktreePath must be rejected to prevent `git -C` parent-repo traversal.
        Assert.Throws<ArgumentException>(() => GitHubRepositorySource.ValidateLocalWorktreePath("relative/path"));
        var nonGitDir = Path.Combine(_tempDir, "not-a-repo");
        Directory.CreateDirectory(nonGitDir);
        Assert.Throws<ArgumentException>(() => GitHubRepositorySource.ValidateLocalWorktreePath(nonGitDir));
    }

    [Fact]
    public async Task GitHubRepositorySource_RunGitProcessAsync_KillsChildProcessOnTimeout()
    {
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("ping -n 10 127.0.0.1 >nul");
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("sleep 10");
        }

        var ex = await Assert.ThrowsAsync<GitHubUnavailableException>(() =>
            GitHubRepositorySource.RunGitProcessAsync(psi, TimeSpan.FromMilliseconds(150), Ct));
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EngineerPipelineStages_WithNativeRepository_PushesLocalWorktreeAndCreatesDraftPrWithoutGhCli()
    {
        var commitSha = CommitSha.Parse(new string('b', 40));
        var lease = new WorkTreeLease(
            RunId: "run-a4",
            RepositoryId: "intochat",
            SourceRepoPath: _tempDir,
            TreePath: _tempDir,
            RealGitDirPath: Path.Combine(_tempDir, ".git"),
            GitPointerSha256Hex: new string('0', 64),
            BaseBranch: "master",
            BaseCommitSha: new string('0', 40),
            BranchName: "ino/feat-native-pr",
            GitHubRepo: "intochat/intochat");
        var changeSet = ChangeSet.Create(
            commitSha,
            [RepoPath.Parse("lib/Demo.cs")],
            "+ // demo",
            additions: 1,
            deletions: 0);
        var planId = EngineerFlow.BuildPlanId("run-a4", commitSha.Value);
        var confirmations = new ApprovedConfirmations(planId, "person-vlad");
        var forbiddenHostCliWorkTree = new ThrowingHostPublishWorkTree();
        var nativeRepo = new RecordingNativeRepository();

        var (approvedBy, pushedCommit, pushedRef) = await EngineerPipelineStages.ExecutePushAsync(
            forbiddenHostCliWorkTree,
            confirmations,
            lease,
            changeSet,
            planId,
            ensureCanPublish: (outcome, expectedSha) =>
            {
                Assert.NotNull(outcome);
                Assert.Equal(ConfirmationOutcome.Approved, outcome.Outcome);
                Assert.Equal(commitSha, expectedSha);
                return outcome.Approver?.Id ?? "person-vlad";
            },
            Ct,
            repository: nativeRepo);

        Assert.Equal("person-vlad", approvedBy);
        Assert.Equal(commitSha, pushedCommit);
        Assert.Equal("refs/heads/ino/feat-native-pr", pushedRef);

        var pushReq = Assert.Single(nativeRepo.Pushes);
        Assert.Equal("ino/feat-native-pr", pushReq.Branch);
        Assert.Equal(commitSha.Value, pushReq.CommitSha);
        Assert.Equal(_tempDir, pushReq.LocalWorktreePath);

        var prUrl = await EngineerPipelineStages.ExecutePullRequestAsync(
            forbiddenHostCliWorkTree,
            lease,
            changeSet,
            approverId: approvedBy,
            planId: planId,
            goal: "Ship ACA Job devslot and native PRs",
            summary: "Wired native IRepository push and Draft PR creation",
            modelId: "test-model",
            engineerContext: null,
            Ct,
            repository: nativeRepo);

        Assert.Equal("https://github.com/intochat/intochat/pull/570", prUrl);
        var prReq = Assert.Single(nativeRepo.PullRequests);
        Assert.True(prReq.Draft);
        Assert.Equal("ino/feat-native-pr", prReq.HeadBranch);
        Assert.Equal("master", prReq.BaseBranch);
        Assert.Contains(commitSha.Value, prReq.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EngineerFlow_UsesInjectedNativeRepositoryForPushAndPr_AndFallsBackCleanlyWhenAbsent()
    {
        var commitSha = CommitSha.Parse(new string('c', 40));
        var lease = new WorkTreeLease(
            RunId: "run-flow",
            RepositoryId: "intochat",
            SourceRepoPath: _tempDir,
            TreePath: _tempDir,
            RealGitDirPath: Path.Combine(_tempDir, ".git"),
            GitPointerSha256Hex: new string('0', 64),
            BaseBranch: "master",
            BaseCommitSha: new string('0', 40),
            BranchName: "ino/feat-engineer-native",
            GitHubRepo: "intochat/intochat");
        var changeSet = ChangeSet.Create(
            commitSha,
            [RepoPath.Parse("lib/Demo.cs")],
            "+ // demo",
            additions: 1,
            deletions: 0);

        // 1. With IRepository registered in DI, EngineerFlow pushes and opens Draft PR natively without touching WorkTree push/gh CLI.
        var nativeRepo = new RecordingNativeRepository();
        var planId = EngineerFlow.BuildPlanId("run-flow", commitSha.Value);
        var services = new ServiceCollection();
        services.AddSingleton<IWorkTree>(new ThrowingHostPublishWorkTree());
        services.AddSingleton<IConfirmationRequests>(new ApprovedConfirmations(planId, "person-vlad"));
        services.AddSingleton<IRepository>(nativeRepo);
        using var provider = services.BuildServiceProvider();

        var flowWithRepo = ActivatorUtilities.CreateInstance<EngineerFlow>(provider);

        var plan = new EngineerTaskPlan(
            "Add native PR support",
            "Wire native IRepository",
            "Approved",
            [],
            [new EngineerTaskItem("t1", "Wire native PR", "Wire native PR", ["lib/Demo.cs"], [], "done")]);
        var run = EngineerRun.Start("run-flow", RepositoryId.Parse("intochat"), "Add native PR support")
            .WithPlan(plan)
            .WithLeases("run-flow", [lease]);
        var updatedInstance = run.Instances[0] with
        {
            Commit = changeSet,
            Edit = new CoderEdit("Native PR", "test-model", [RepoPath.Parse("lib/Demo.cs")]),
            Build = new EngineerBuildReport(true, true, true, true, 1, 0, 0, 0, [], 10, "ok"),
            Live = new EngineerLiveReport(true, true, true, true, 0, 0, 0, 0, [], [], "ok"),
            Status = "verified",
        };
        run = run.WithUpdatedInstances([updatedInstance], 0)
            .WithApprovalPending(planId, DateTimeOffset.UtcNow.AddHours(1));

        var pushStepMethod = typeof(EngineerFlow).GetMethod("PushStepAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var prStepMethod = typeof(EngineerFlow).GetMethod("PullRequestStepAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var pushResult = await (Task<DigitalBrain.Flows.StepResult<EngineerRun>>)pushStepMethod.Invoke(flowWithRepo, [run, Ct])!;
        Assert.Equal("refs/heads/ino/feat-engineer-native", pushResult.Next.Publication?.PushedRef);
        Assert.Single(nativeRepo.Pushes);

        var prResult = await (Task<DigitalBrain.Flows.StepResult<EngineerRun>>)prStepMethod.Invoke(flowWithRepo, [pushResult.Next, Ct])!;
        Assert.Equal("https://github.com/intochat/intochat/pull/570", prResult.Next.Publication?.PullRequestUrl);
        Assert.Single(nativeRepo.PullRequests);

        // 2. Without IRepository configured, EngineerFlow falls back cleanly to IWorkTree.
        var fallbackWorkTree = new RecordingFallbackWorkTree();
        var fallbackServices = new ServiceCollection();
        fallbackServices.AddSingleton<IWorkTree>(fallbackWorkTree);
        fallbackServices.AddSingleton<IConfirmationRequests>(new ApprovedConfirmations(planId, "person-vlad"));
        using var fallbackProvider = fallbackServices.BuildServiceProvider();
        var flowWithoutRepo = ActivatorUtilities.CreateInstance<EngineerFlow>(fallbackProvider);

        var fallbackPush = await (Task<DigitalBrain.Flows.StepResult<EngineerRun>>)pushStepMethod.Invoke(flowWithoutRepo, [run, Ct])!;
        Assert.Equal("refs/heads/ino/feat-engineer-native", fallbackPush.Next.Publication?.PushedRef);
        Assert.Equal(1, fallbackWorkTree.PushCount);

        var fallbackPr = await (Task<DigitalBrain.Flows.StepResult<EngineerRun>>)prStepMethod.Invoke(flowWithoutRepo, [fallbackPush.Next, Ct])!;
        Assert.Equal("https://github.com/intochat/intochat/pull/999", fallbackPr.Next.Publication?.PullRequestUrl);
        Assert.Equal(1, fallbackWorkTree.PrCount);
    }

    private sealed class FakeGitHubIntegrations(string appId, string privateKeyPem)
        : DigitalBrain.Capabilities.Connections.Integrations.IModuleIntegrations<GitHubModule>
    {
        public DigitalBrain.Capabilities.Connections.Integrations.IIntegrationHandle Get(string integrationId)
            => new FakeHandle(appId, privateKeyPem);

        private sealed class FakeHandle(string appId, string privateKeyPem)
            : DigitalBrain.Capabilities.Connections.Integrations.IIntegrationHandle
        {
            public string IntegrationId => "github";

            public Task<DigitalBrain.Capabilities.Connections.Integrations.IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new DigitalBrain.Capabilities.Connections.Integrations.IntegrationSnapshot
                {
                    Status = DigitalBrain.Capabilities.Connections.Integrations.IntegrationStatus.Ready,
                    Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["AppId"] = appId },
                });

            public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
                => Task.FromResult(privateKeyPem);

            public Task<DigitalBrain.Capabilities.Connections.Integrations.ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new DigitalBrain.Capabilities.Connections.Integrations.ReleasedIntegration
                {
                    Values = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["AppId"] = appId,
                        ["PrivateKeyPem"] = privateKeyPem,
                    },
                    Snapshot = new DigitalBrain.Capabilities.Connections.Integrations.IntegrationSnapshot
                    {
                        Status = DigitalBrain.Capabilities.Connections.Integrations.IntegrationStatus.Ready,
                        Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["AppId"] = appId },
                    },
                });
        }
    }

    private sealed class FakeTokenMintHandler(string token, long repoId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O");
            var json = $$"""
                {
                  "token": "{{token}}",
                  "expires_at": "{{expiresAt}}",
                  "repositories": [ { "id": {{repoId}} } ],
                  "permissions": { "contents": "write", "metadata": "read" }
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ApprovedConfirmations(string planId, string approverId) : IConfirmationRequests
    {
        public Task<ConfirmationResult?> Request(ConfirmationPlan plan) => Task.FromResult<ConfirmationResult?>(null);
        public Task Withdraw(string id) => Task.CompletedTask;
        public Task<ConfirmationResult?> Outcome(string id)
            => Task.FromResult<ConfirmationResult?>(
                string.Equals(id, planId, StringComparison.Ordinal)
                    ? new ConfirmationResult(id, ConfirmationOutcome.Approved, ConfirmationDenyReason.None, Skipped: false)
                    {
                        Approver = new ConfirmationPrincipal(approverId, PrincipalKind.Person),
                    }
                    : null);
    }

    private sealed class ThrowingHostPublishWorkTree : IWorkTree
    {
        public IReadOnlyList<RegisteredRepository> ListRepositories() => [];
        public void RegisterRepository(RegisteredRepository repository) => throw new NotSupportedException();
        public RegisteredRepository ResolveRepository(RepositoryId repositoryId) => throw new NotSupportedException();
        public Task<WorkTreeLease> PrepareAsync(RepositoryId repositoryId, string runId, string goal, BranchName? baseBranch = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChangeSet> VerifyAndCommitAsync(WorkTreeLease lease, string commitMessage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> PushAsync(WorkTreeLease lease, CommitSha approvedSha, CancellationToken ct = default)
            => throw new InvalidOperationException("Host git push must not be called when native IRepository is available.");
        public Task<string?> CreateDraftPullRequestAsync(WorkTreeLease lease, string prTitle, string prBody, CancellationToken ct = default)
            => throw new InvalidOperationException("Host gh CLI must not be called when native IRepository is available.");
        public Task<PublishedPrResult> PublishAsync(WorkTreeLease lease, CommitSha approvedSha, string prTitle, string prBody, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CleanupAsync(WorkTreeLease lease, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingFallbackWorkTree : IWorkTree
    {
        public int PushCount { get; private set; }
        public int PrCount { get; private set; }

        public IReadOnlyList<RegisteredRepository> ListRepositories() => [];
        public void RegisterRepository(RegisteredRepository repository) => throw new NotSupportedException();
        public RegisteredRepository ResolveRepository(RepositoryId repositoryId) => throw new NotSupportedException();
        public Task<WorkTreeLease> PrepareAsync(RepositoryId repositoryId, string runId, string goal, BranchName? baseBranch = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChangeSet> VerifyAndCommitAsync(WorkTreeLease lease, string commitMessage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> PushAsync(WorkTreeLease lease, CommitSha approvedSha, CancellationToken ct = default)
        {
            PushCount++;
            return Task.FromResult($"refs/heads/{lease.BranchName}");
        }
        public Task<string?> CreateDraftPullRequestAsync(WorkTreeLease lease, string prTitle, string prBody, CancellationToken ct = default)
        {
            PrCount++;
            return Task.FromResult<string?>("https://github.com/intochat/intochat/pull/999");
        }
        public Task<PublishedPrResult> PublishAsync(WorkTreeLease lease, CommitSha approvedSha, string prTitle, string prBody, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CleanupAsync(WorkTreeLease lease, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingNativeRepository : IRepository
    {
        public List<PushBranchRequest> Pushes { get; } = [];
        public List<CreatePullRequest> PullRequests { get; } = [];

        public Task<RepositoryView> Refresh(RefreshRepository request) => throw new NotSupportedException();
        public Task<RepositoryView> Read() => Task.FromResult(new RepositoryView("connection/github/main", true, [], null));
        public Task<PullRequestRead> ReadPullRequest(ReadPullRequest query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PullRequestsRead> ReadPullRequests() => throw new NotSupportedException();
        public Task<ReviewEvidenceRead> ReadReviewEvidence(ReadReviewEvidence query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RequiredChecksRead> ReadRequiredChecks(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PushBranch(PushBranchRequest request)
        {
            Pushes.Add(request);
            return Task.CompletedTask;
        }
        public Task<PullRequestView> CreatePullRequest(CreatePullRequest request)
        {
            PullRequests.Add(request);
            return Task.FromResult(new PullRequestView(570, request.Title, "https://github.com/intochat/intochat/pull/570", "head", "base", true, request.Draft, null));
        }
        public Task ReplyToReviewComment(ReplyReviewComment request) => throw new NotSupportedException();
        public Task MergePullRequest(MergePullRequest request) => throw new NotSupportedException();
    }
}
