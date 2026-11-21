using IntoChat.OS.Tests;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

public sealed class FeatureToPrFlowFacts : IDisposable
{
    private readonly string _root;

    public FeatureToPrFlowFacts()
    {
        _root = Path.Combine(Path.GetTempPath(), "intochat-f2pr-int-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class FeatureToPrFixture(IWorkTree workTree, ICoder coder) : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlowsFactsModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            services.AddJournalPayloads(typeof(IFlows).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddJournalPayloads(typeof(TestFlow).Assembly);
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton(workTree);
            services.AddSingleton(coder);
        }
    }

    private sealed class DeterministicCoder(Action<WorkTreeLease> editAction, string summary = "Implemented requested C# change cleanly inside confined worktree.") : ICoder
    {
        public Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            editAction(lease);
            return Task.FromResult(new CoderEditResult(
                Summary: summary,
                TouchedFiles: ["code/Calculator.cs"],
                ModelId: "qwen2.5-coder:14b"));
        }
    }

    private sealed class CrashOnceBetweenPushAndPrWorkTree(IWorkTree inner) : IWorkTree
    {
        private int _crashedAfterPush;
        private int _crashedAfterPr;

        public IReadOnlyList<RegisteredRepository> ListRepositories() => inner.ListRepositories();
        public void RegisterRepository(RegisteredRepository repository) => inner.RegisterRepository(repository);
        public RegisteredRepository ResolveRepository(RepositoryId repositoryId) => inner.ResolveRepository(repositoryId);
        public Task<WorkTreeLease> PrepareAsync(RepositoryId repositoryId, string runId, string goal, BranchName? baseBranch = null, CancellationToken ct = default)
            => inner.PrepareAsync(repositoryId, runId, goal, baseBranch, ct);
        public Task<ChangeSet> VerifyAndCommitAsync(WorkTreeLease lease, string commitMessage, CancellationToken ct = default)
            => inner.VerifyAndCommitAsync(lease, commitMessage, ct);
        public Task<PublishedPrResult> PublishAsync(WorkTreeLease lease, CommitSha approvedSha, string prTitle, string prBody, CancellationToken ct = default)
            => inner.PublishAsync(lease, approvedSha, prTitle, prBody, ct);
        public Task CleanupAsync(WorkTreeLease lease, CancellationToken ct = default)
            => inner.CleanupAsync(lease, ct);

        public async Task<string> PushAsync(WorkTreeLease lease, CommitSha approvedSha, CancellationToken ct = default)
        {
            var pushedRef = await inner.PushAsync(lease, approvedSha, ct).ConfigureAwait(false);
            // Simulate a process/activation crash right AFTER git push succeeded on origin, BEFORE Flow saved PushedRef!
            if (Interlocked.Exchange(ref _crashedAfterPush, 1) == 0)
            {
                throw new OperationCanceledException("Simulated crash immediately after git push before checkpoint persistence.");
            }

            return pushedRef;
        }

        public async Task<string?> CreateDraftPullRequestAsync(WorkTreeLease lease, string prTitle, string prBody, CancellationToken ct = default)
        {
            var prUrl = await inner.CreateDraftPullRequestAsync(lease, prTitle, prBody, ct).ConfigureAwait(false);
            // Simulate a second crash right AFTER `gh pr create --draft` created the PR, BEFORE Flow saved PullRequestUrl!
            if (Interlocked.Exchange(ref _crashedAfterPr, 1) == 0)
            {
                throw new OperationCanceledException("Simulated crash immediately after gh pr create before checkpoint persistence.");
            }

            return prUrl;
        }
    }

    private (string BareOriginPath, string SourceCheckoutPath, WorkTreeService WorkTree) CreateBareOriginAndWorkTreeService(
        string? gitHubRepo = null,
        string? toolPath = null)
    {
        var bareOrigin = Path.Combine(_root, "origin.git");
        var checkout = Path.Combine(_root, "source-repo");
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(bareOrigin);
        Directory.CreateDirectory(checkout);
        Directory.CreateDirectory(home);

        RunProcess("git", ["init", "--bare", "--initial-branch=main"], bareOrigin);
        RunProcess("git", ["init", "--initial-branch=main"], checkout);
        RunProcess("git", ["config", "user.name", "Repo Owner"], checkout);
        RunProcess("git", ["config", "user.email", "owner@example.test"], checkout);

        Directory.CreateDirectory(Path.Combine(checkout, "code"));
        File.WriteAllText(Path.Combine(checkout, "code", "Calculator.cs"), """
            namespace Demo;

            public static class Calculator
            {
                public static int Add(int a, int b) => a + b;
            }
            """);

        RunProcess("git", ["add", "."], checkout);
        RunProcess("git", ["commit", "-m", "Initial commit"], checkout);
        RunProcess("git", ["remote", "add", "origin", bareOrigin], checkout);
        RunProcess("git", ["push", "-u", "origin", "main"], checkout);

        var wt = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home,
            ToolPath = toolPath,
        });
        wt.RegisterRepository(new RegisteredRepository("os", checkout, GitHubRepo: gitHubRepo, DefaultBaseBranch: "main"));
        return (bareOrigin, checkout, wt);
    }

    [Fact]
    public async Task FeatureToPrFlowParksOnShaBoundPersonConfirmationRejectsNeuronBypassAndPublishesOnPersonApproval()
    {
        var (bareOrigin, _, workTree) = CreateBareOriginAndWorkTreeService();
        var coder = new DeterministicCoder(lease =>
        {
            File.WriteAllText(Path.Combine(lease.TreePath, "code", "Calculator.cs"), """
                namespace Demo;

                public static class Calculator
                {
                    public static int Add(int a, int b) => a + b;
                    public static int Multiply(int a, int b) => a * b;
                }
                """);
        });

        await using var fixture = new FeatureToPrFixture(workTree, coder);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var studio = runtime.Get<IStudio>(new NeuronId(runtime.Brain, "ino.studio", "studio"));

        InoToolResult proposedResult;
        using (runtime.Edge.Enter(runtime.Edge.Person("person", "person", runtime.Brain)))
        {
            proposedResult = await studio.ProposeChange("os", "Add Multiply method to Calculator", Ct);
        }

        Assert.True(proposedResult.Ok, proposedResult.Text);
        Assert.NotNull(proposedResult.Card);
        Assert.Equal("approval", proposedResult.Card!.Kind);
        Assert.False(proposedResult.Card.Title.StartsWith("Approve", StringComparison.Ordinal), proposedResult.Card.Title);
        Assert.NotNull(proposedResult.Card.SurfaceScopeUri);
        var hashIdx = proposedResult.Card.SurfaceScopeUri!.IndexOf('#', StringComparison.Ordinal);
        Assert.True(hashIdx >= 0);
        var flowId = proposedResult.Card.SurfaceScopeUri[(hashIdx + 1)..];

        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var waitingView = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status == FlowStatus.Waiting && v.WaitingStep == "approval-gate");

        Assert.NotNull(waitingView.ConfirmationPlanId);
        Assert.StartsWith("app:change:", waitingView.ConfirmationPlanId!, StringComparison.Ordinal);

        var starter = runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId));
        var headBeforeApproval = await starter.Head();
        var changeBeforeApproval = JsonSerializer.Deserialize<Change>(headBeforeApproval.InnerJson, FeatureToPrFlow.Json)!;

        Assert.NotNull(changeBeforeApproval.Commit);
        Assert.Equal(40, changeBeforeApproval.Commit!.Commit.Value.Length);
        Assert.EndsWith(":" + changeBeforeApproval.Commit.Commit.Value, waitingView.ConfirmationPlanId!, StringComparison.Ordinal);
        Assert.Equal([RepoPath.Parse("code/Calculator.cs")], changeBeforeApproval.Commit.ChangedFiles);
        Assert.Contains("+    public static int Multiply", changeBeforeApproval.Commit.HostRenderedDiff, StringComparison.Ordinal);
        Assert.Null(changeBeforeApproval.Publication);

        // What the model gets back names the change; the diff stays on the approval.
        Assert.DoesNotContain(changeBeforeApproval.Commit.HostRenderedDiff.TrimEnd(), proposedResult.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("+    public static int Multiply", proposedResult.Text, StringComparison.Ordinal);

        // Verify Wave 0 B05 hardening: a neuron/agent caller attempting to Resolve a RequiresPerson=true plan
        // is rejected with UnauthorizedAccessException and leaves the human's plan pending & untouched!
        var resolver = runtime.Get<IntoChat.OS.Modules.Apps.Tests.Integration.Approvals.IPlanResolver>(
            new NeuronId(runtime.Brain, IntoChat.OS.Modules.Apps.Tests.Integration.Approvals.PlanResolver.Alias, "resolver"));
        using (runtime.Edge.Enter(runtime.Edge.Person("person", "person", runtime.Brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                resolver.Resolve(waitingView.ConfirmationPlanId!, approve: true));
        }

        // An agent's cancel of the waiting run is refused: the run still waits and its tree stays.
        using (runtime.Edge.Enter(runtime.Edge.Agent(new Actor.Person("person", "person"), runtime.Brain, conversation: "ino/conversations/main")))
        {
            var refused = await Assert.ThrowsAsync<DigitalBrain.Rules.RuleViolationException>(() => flows.Cancel(flowId, "agent says stop", Ct));
            Assert.Equal(["apps.flows-person-cancels-approval-wait"], refused.Rejected.Rules);
            Assert.Equal($"flow {flowId} waits for a person at approval-gate: only a person cancels it", refused.Message);
        }

        Assert.Equal(FlowStatus.Waiting, (await starter.Head()).Status);
        Assert.Empty(await runtime.Signals<FlowDone>(FlowTypeFacts.Id(flowId), Ct));
        Assert.True(Directory.Exists(changeBeforeApproval.Tree!.TreePath));

        // Before Person approval, remote bare repo MUST NOT have the branch and flow MUST still be Waiting!
        var branchesBefore = RunProcess("git", ["branch", "--list", "ino/*"], bareOrigin).Trim();
        Assert.Equal("", branchesBefore);
        Assert.Equal(FlowStatus.Waiting, (await flows.Read(flowId, Ct))!.Status);

        // Approve as real human Person through the trusted runtime edge
        await runtime.ApproveAsPerson(waitingView.ConfirmationPlanId!, Ct);

        var completedView = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status == FlowStatus.Completed);

        Assert.Equal(FeatureToPrFlow.Names, completedView.Done);

        var headAfter = await starter.Head();
        var changeAfter = JsonSerializer.Deserialize<Change>(headAfter.InnerJson, FeatureToPrFlow.Json)!;

        Assert.NotNull(changeAfter.Approval?.ApprovedBy);
        Assert.Equal($"refs/heads/{changeAfter.Tree!.BranchName}", changeAfter.Publication?.PushedRef);
        Assert.True(changeAfter.Publication?.PullRequestCompleted);
        Assert.True(changeAfter.Publication?.Cleaned);
        Assert.False(Directory.Exists(changeAfter.Tree.TreePath));

        // Verify bare remote received the exact approved SHA!
        var pushedSha = RunProcess("git", ["rev-parse", changeAfter.Publication!.PushedRef!], bareOrigin).Trim();
        Assert.Equal(changeBeforeApproval.Commit.Commit.Value, pushedSha);
    }

    [Fact]
    public async Task FeatureToPrFlowPublishesDraftPrViaScriptedGhStripsSkipCiAndResumesIdempotentlyAfterCrashes()
    {
        var ghBinDir = BuildScriptedGhBinary(out var ghLogPath, out var ghPrStorePath);
        var (bareOrigin, _, realWorkTree) = CreateBareOriginAndWorkTreeService(gitHubRepo: "intochat/os", toolPath: ghBinDir);
        var faultWorkTree = new CrashOnceBetweenPushAndPrWorkTree(realWorkTree);

        var coder = new DeterministicCoder(
            lease =>
            {
                File.WriteAllText(Path.Combine(lease.TreePath, "code", "Calculator.cs"), """
                    namespace Demo;

                    public static class Calculator
                    {
                        public static int Add(int a, int b) => a + b;
                        public static int Divide(int a, int b) => a / b;
                    }
                    """);
            },
            summary: "Added Divide method cleanly [skip ci]\nskip-checks: true");

        var siloHome = Path.Combine(_root, "silo-home");
        await using var fixture = new FeatureToPrFixture(faultWorkTree, coder);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct, home: siloHome);
        using var person = runtime.As(Who.Person);

        var studio = runtime.Get<IStudio>(new NeuronId(runtime.Brain, "ino.studio", "studio"));
        InoToolResult proposed;
        using (runtime.Edge.Enter(runtime.Edge.Person("person", "person", runtime.Brain)))
        {
            proposed = await studio.ProposeChange("os", "Add Divide method [ci skip]", Ct);
        }

        Assert.True(proposed.Ok, proposed.Text);
        var flowId = proposed.Card!.SurfaceScopeUri![(proposed.Card.SurfaceScopeUri!.IndexOf('#', StringComparison.Ordinal) + 1)..];
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status == FlowStatus.Waiting && v.WaitingStep == "approval-gate");

        // Approve — triggers push (succeeds on bareOrigin then throws simulated crash before saving PushedRef)
        await runtime.ApproveAsPerson(waiting.ConfirmationPlanId!, Ct);

        // Wait until first push interruption is recorded (bareOrigin already has the branch, flow is still Running on "push")
        var afterPushCrash = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status == FlowStatus.Running && !string.IsNullOrEmpty(RunProcess("git", ["branch", "--list", "ino/*"], bareOrigin).Trim()));
        Assert.DoesNotContain("push", afterPushCrash.Done);

        // Resume after crash #1 (between git push and saving checkpoint): "push" recovers idempotently, then "pr" creates draft PR and crashes before saving!
        var callbacks = runtime.Get<IFlowCallbacks>(FlowTypeFacts.Id(flowId));
        await callbacks.Advance();

        var afterPrCrash = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status == FlowStatus.Running && v.Done.Contains("push", StringComparer.Ordinal) && File.Exists(ghPrStorePath));
        Assert.DoesNotContain("pr", afterPrCrash.Done);

        // Resume after crash #2 (between gh pr create and saving checkpoint): "pr" recovers existing PR via `gh pr list`, then "clean" removes worktree!
        await callbacks.Advance();

        var completed = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status == FlowStatus.Completed);
        Assert.Equal(FeatureToPrFlow.Names, completed.Done);

        var finalChange = await Inner(runtime, flowId);
        Assert.Equal("https://github.com/intochat/os/pull/404", finalChange.Publication?.PullRequestUrl);
        Assert.True(finalChange.Publication?.PullRequestCompleted);
        Assert.True(finalChange.Publication?.Cleaned);
        Assert.False(Directory.Exists(finalChange.Tree!.TreePath));

        // Verify commit message on bare origin stripped [skip ci], [ci skip], and skip-checks: true
        var commitMsg = RunProcess("git", ["log", "-1", "--format=%B", finalChange.Commit!.Commit.Value], bareOrigin);
        Assert.Contains("Add Divide method", commitMsg, StringComparison.Ordinal);
        Assert.Contains("Added Divide method cleanly", commitMsg, StringComparison.Ordinal);
        Assert.DoesNotContain("[skip ci]", commitMsg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[ci skip]", commitMsg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("skip-checks", commitMsg, StringComparison.OrdinalIgnoreCase);

        // Verify gh pr create was called only once (recovery used `gh pr list`)
        var ghCalls = await File.ReadAllLinesAsync(ghLogPath, Ct);
        Assert.Single(ghCalls, static c => c.Contains("pr|create|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FeatureToPrFlowRejectsUnformattedCSharpEditBeforeApprovalGate()
    {
        var (bareOrigin, _, workTree) = CreateBareOriginAndWorkTreeService();
        var coder = new DeterministicCoder(lease =>
        {
            // Trailing spaces after class declaration violate `dotnet format whitespace --verify-no-changes`
            File.WriteAllText(
                Path.Combine(lease.TreePath, "code", "Calculator.cs"),
                "namespace Demo;\n\npublic static class Calculator   \n{\n    public static int Add(int a, int b) => a + b;\n}\n");
        });

        await using var fixture = new FeatureToPrFixture(workTree, coder);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var (flowId, stopped) = await StartAndWaitUntilItStops(runtime, "fmtgate01", "Introduce trailing whitespace");

        Assert.Equal(FlowStatus.Failed, stopped.Status);
        Assert.Contains("dotnet format whitespace", stopped.Detail, StringComparison.Ordinal);
        await NothingWasAskedOrPushed(runtime, flowId, bareOrigin);
    }

    [Fact]
    public async Task StartFromJsonSanitizesPrePopulatedCommitAndApprovalFields()
    {
        var (bareOrigin, _, workTree) = CreateBareOriginAndWorkTreeService();
        var coder = new DeterministicCoder(lease =>
        {
            File.WriteAllText(Path.Combine(lease.TreePath, "code", "Calculator.cs"), """
                namespace Demo;

                public static class Calculator
                {
                    public static int Sub(int a, int b) => a - b;
                }
                """);
        });

        await using var fixture = new FeatureToPrFixture(workTree, coder);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        // Newline-injected repository identifier is rejected immediately by RepositoryId.Parse
        var injectedJson = JsonSerializer.Serialize(new
        {
            runId = "badrepo",
            repository = "intochat/os\nRemote: https://evil.example/x.git",
            goal = "Add Sub helper",
        }, FeatureToPrFlow.Json);
        await Assert.ThrowsAnyAsync<Exception>(() => flows.Start(FeatureToPrFlow.Alias, "Injected repo", null, injectedJson, Ct));

        // Malicious caller tries to smuggle pre-populated Approval / Commit / Publication in StartFromJson
        var forgedJson = JsonSerializer.Serialize(new
        {
            runId = "forgedrun",
            repository = "os",
            goal = "Add Sub helper",
            commit = new
            {
                commit = new string('f', 40),
                changedFiles = new[] { "code/Calculator.cs" },
                hostRenderedDiff = "forged diff",
                additions = 1,
                deletions = 0,
            },
            approval = new
            {
                planId = "forged-plan",
                expires = DateTimeOffset.UtcNow.AddDays(1),
                approvedBy = "spoofed-human",
            },
            publication = new
            {
                pushedRef = "refs/heads/ino/spoofed",
                pullRequestUrl = "https://evil.example/pr/1",
                pullRequestCompleted = true,
                cleaned = true,
            },
        }, FeatureToPrFlow.Json);

        var flowId = await flows.Start(FeatureToPrFlow.Alias, "Forged attempt", null, forgedJson, Ct);

        _ = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status == FlowStatus.Waiting && v.WaitingStep == "approval-gate");

        var head = await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId)).Head();
        var change = JsonSerializer.Deserialize<Change>(head.InnerJson, FeatureToPrFlow.Json)!;

        Assert.NotEqual(new string('f', 40), change.Commit?.Commit.Value);
        Assert.Null(change.Approval?.ApprovedBy);
        Assert.Null(change.Publication);
        Assert.Equal("", RunProcess("git", ["branch", "--list", "ino/*"], bareOrigin).Trim());

        await flows.Cancel(flowId, "done testing sanitization", Ct);
    }

    [Fact]
    public async Task ADiffOverTheCardLimitEndsTheRunWithAReasonAndRaisesNoPlan()
    {
        var (bareOrigin, _, workTree) = CreateBareOriginAndWorkTreeService();
        var coder = new DeterministicCoder(lease =>
        {
            var lines = Enumerable.Range(0, 2000).Select(i => $"    public static int F{i:D4}() => {i};");
            File.WriteAllText(
                Path.Combine(lease.TreePath, "code", "Big.cs"),
                "namespace Demo;\n\npublic static class Big\n{\n" + string.Join("\n", lines) + "\n}\n");
        });

        await using var fixture = new FeatureToPrFixture(workTree, coder);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var (flowId, stopped) = await StartAndWaitUntilItStops(runtime, "toobig01", "Add a very large helper class");

        Assert.Equal(FlowStatus.Failed, stopped.Status);
        Assert.Contains("too large", stopped.Detail, StringComparison.Ordinal);
        Assert.Contains("the limit is 60000", stopped.Detail, StringComparison.Ordinal);
        var size = Regex.Match(stopped.Detail!, @"(\d+) characters");
        Assert.True(size.Success, stopped.Detail);
        Assert.True(int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) > 60_000, stopped.Detail);

        await NothingWasAskedOrPushed(runtime, flowId, bareOrigin);
    }

    [Fact]
    public async Task UnderTheLimitThePlanBodyHoldsEveryFileNameAndTheFullDiff()
    {
        const string Marker = "    public static string LastLine() => \"the-last-added-line\";";
        var (bareOrigin, _, workTree) = CreateBareOriginAndWorkTreeService();
        var coder = new DeterministicCoder(lease =>
        {
            var code = Path.Combine(lease.TreePath, "code");
            File.WriteAllText(Path.Combine(code, "Calculator.cs"), """
                namespace Demo;

                public static class Calculator
                {
                    public static int Add(int a, int b) => a + b;
                    public static int Multiply(int a, int b) => a * b;
                }
                """);
            foreach (var name in new[] { "Alpha", "Beta" })
            {
                var members = Enumerable.Range(0, 30).Select(i => $"    public static int {name}{i:D2}() => {i};");
                File.WriteAllText(
                    Path.Combine(code, name + ".cs"),
                    $"namespace Demo;\n\npublic static class {name}\n{{\n" + string.Join("\n", members) + "\n}\n");
            }

            File.WriteAllText(
                Path.Combine(code, "Zeta.cs"),
                "namespace Demo;\n\npublic static class Zeta\n{\n" + Marker + "\n}\n");
        });

        await using var fixture = new FeatureToPrFixture(workTree, coder);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        const string Goal = "Add three helper classes and Multiply\nand a second line only the pull request would show";
        var (flowId, stopped) = await StartAndWaitUntilItStops(
            runtime,
            "whole01",
            Goal,
            repository: "intochat/os");

        Assert.Equal(FlowStatus.Waiting, stopped.Status);
        Assert.Equal("approval-gate", stopped.WaitingStep);
        var change = await Inner(runtime, flowId);
        Assert.Equal(
            [RepoPath.Parse("code/Alpha.cs"), RepoPath.Parse("code/Beta.cs"), RepoPath.Parse("code/Calculator.cs"), RepoPath.Parse("code/Zeta.cs")],
            change.Commit!.ChangedFiles);
        Assert.True(change.Commit.HostRenderedDiff.Length > 2400, $"The diff has {change.Commit.HostRenderedDiff.Length} characters.");

        var plan = await Plan(runtime, stopped.ConfirmationPlanId!);
        var body = plan.Body;
        var lines = body.Split('\n').Select(line => line.Trim()).ToArray();
        Assert.Contains(" on os (", plan.Title, StringComparison.Ordinal);
        Assert.Contains("Repository: os", lines);
        Assert.Equal("Remote: " + bareOrigin, Assert.Single(lines, line => line.StartsWith("Remote: ", StringComparison.Ordinal)));
        Assert.Contains("> and a second line only the pull request would show", lines);
        Assert.All(change.Commit.ChangedFiles, file => Assert.Contains(file.Value, lines));
        Assert.Equal(40, change.Commit.Commit.Value.Length);
        Assert.Contains(change.Commit.Commit.Value, body, StringComparison.Ordinal);
        Assert.Contains(change.Tree!.BaseBranch, body, StringComparison.Ordinal);
        Assert.Contains(change.Tree.BaseCommitSha, body, StringComparison.Ordinal);
        Assert.Contains(change.Tree.BranchName, body, StringComparison.Ordinal);
        Assert.Contains(bareOrigin, body, StringComparison.Ordinal);
        Assert.Contains(change.Commit.HostRenderedDiff.TrimEnd(), body, StringComparison.Ordinal);
        Assert.Contains(Marker, body, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFileThatCannotBeShownAsTextEndsTheRunBeforeAnyPlan()
    {
        var (bareOrigin, _, workTree) = CreateBareOriginAndWorkTreeService();
        var coder = new DeterministicCoder(lease =>
            File.WriteAllBytes(Path.Combine(lease.TreePath, "code", "blob.txt"), [0x50, 0x4B, 0x00, 0x00, 0x01, 0x02, 0x00, 0xFF]));

        await using var fixture = new FeatureToPrFixture(workTree, coder);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = runtime.As(Who.Person);

        var (flowId, stopped) = await StartAndWaitUntilItStops(runtime, "binary01", "Add a data file");

        Assert.Equal(FlowStatus.Failed, stopped.Status);
        Assert.Contains("cannot be shown as text", stopped.Detail, StringComparison.Ordinal);
        Assert.Contains("code/blob.txt", stopped.Detail, StringComparison.Ordinal);

        await NothingWasAskedOrPushed(runtime, flowId, bareOrigin);
    }

    private static async Task<(string FlowId, FlowView Stopped)> StartAndWaitUntilItStops(
        INeuronRuntime runtime,
        string runId,
        string goal,
        string repository = "os")
    {
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var json = JsonSerializer.Serialize(Change.Start(runId, RepositoryId.Parse(repository), goal), FeatureToPrFlow.Json);
        var flowId = await flows.Start(FeatureToPrFlow.Alias, goal, null, json, Ct);
        var stopped = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status == FlowStatus.Waiting || v is { Status: FlowStatus.Failed, Detail.Length: > 0 });
        return (flowId, stopped);
    }

    private static async Task<Change> Inner(INeuronRuntime runtime, string flowId)
    {
        var head = await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId)).Head();
        return JsonSerializer.Deserialize<Change>(head.InnerJson, FeatureToPrFlow.Json)!;
    }

    private static IConfirmationInbox Inbox(INeuronRuntime runtime)
        => runtime.Get<IConfirmationInbox>(new NeuronId(runtime.Brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));

    private static async Task<ConfirmationPlan> Plan(INeuronRuntime runtime, string planId)
    {
        for (var i = 0; i < 400; i++)
        {
            if ((await Inbox(runtime).Read()).Pending.FirstOrDefault(p => p.PlanId == planId) is { } asked)
            {
                return asked.Plan;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail($"The inbox never held plan {planId}.");
        return null!;
    }

    private static async Task NothingWasAskedOrPushed(INeuronRuntime runtime, string flowId, string bareOrigin)
    {
        var change = await Inner(runtime, flowId);
        Assert.Null(change.Approval);
        Assert.Null(change.Commit?.HostRenderedDiff);
        Assert.Null(change.Publication);
        Assert.DoesNotContain((await Inbox(runtime).Read()).Pending, p => p.PlanId.StartsWith("app:change:", StringComparison.Ordinal));
        Assert.Equal("", RunProcess("git", ["branch", "--list", "ino/*"], bareOrigin).Trim());
        if (change.Tree is { } lease)
        {
            Assert.False(Directory.Exists(lease.TreePath));
        }
    }

    private string BuildScriptedGhBinary(out string logPath, out string storePath)
    {
        var srcDir = Path.Combine(_root, "gh-src");
        var binDir = Path.Combine(_root, "gh-bin");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(binDir);

        logPath = Path.Combine(_root, "gh-calls.log");
        storePath = Path.Combine(_root, "gh-prs.txt");

        var escapedLog = logPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        var escapedStore = storePath.Replace("\\", "\\\\", StringComparison.Ordinal);

        File.WriteAllText(
            Path.Combine(srcDir, "gh.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include=\"Program.cs\" /></ItemGroup></Project>");

        var programCs = $$"""
            using System;
            using System.Collections.Generic;
            using System.IO;
            using System.Linq;
            using System.Text.Json;

            internal static class Program
            {
                public static int Main(string[] args)
                {
                    var logPath = "{{escapedLog}}";
                    var storePath = "{{escapedStore}}";
                    var cwd = Directory.GetCurrentDirectory();
                    var emptyEntries = Directory.GetFileSystemEntries(cwd).Length;
                    File.AppendAllText(logPath, string.Join("|", args) + "|cwdEmpty=" + (emptyEntries == 0) + "\n");
                    if (emptyEntries != 0)
                    {
                        Console.Error.WriteLine("Expected empty working directory, found " + emptyEntries + " entries.");
                        return 2;
                    }

                    string? Arg(string name)
                    {
                        for (var i = 0; i < args.Length - 1; i++)
                        {
                            if (args[i] == name) return args[i + 1];
                        }
                        return null;
                    }

                    var repo = Arg("--repo") ?? "";
                    var head = Arg("--head") ?? "";
                    var existing = File.Exists(storePath) ? File.ReadAllLines(storePath).ToList() : new List<string>();
                    var key = repo + "|" + head + "|";
                    var found = existing.FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal));

                    if (args.Length >= 2 && args[0] == "pr" && args[1] == "list")
                    {
                        if (found is not null)
                        {
                            var url = found[key.Length..];
                            Console.WriteLine(JsonSerializer.Serialize(new[] { new { url } }));
                        }
                        else
                        {
                            Console.WriteLine("[]");
                        }
                        return 0;
                    }

                    if (args.Length >= 2 && args[0] == "pr" && args[1] == "create")
                    {
                        if (found is not null)
                        {
                            Console.Error.WriteLine("a pull request for branch '" + head + "' into branch 'main' already exists");
                            return 1;
                        }
                        var prUrl = "https://github.com/" + repo + "/pull/404";
                        File.AppendAllText(storePath, key + prUrl + "\n");
                        Console.WriteLine(prUrl);
                        return 0;
                    }

                    return 1;
                }
            }
            """;
        File.WriteAllText(Path.Combine(srcDir, "Program.cs"), programCs);

        RunProcess("dotnet", ["build", Path.Combine(srcDir, "gh.csproj"), "-c", "Release", "-o", binDir, "-v", "q"], srcDir);
        return binDir;
    }

    private static string RunProcess(string fileName, IReadOnlyList<string> args, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}\n{stdout}");
        }

        return stdout;
    }
}
