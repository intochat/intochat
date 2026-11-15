using System.Diagnostics;
using System.Text.Json;
using DigitalBrain;
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

    private sealed class FeatureToPrFixture(WorkTreeService workTree, ICoder coder) : BothRuntimesFixture
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
            services.AddSingleton<IWorkTree>(workTree);
            services.AddSingleton(coder);
        }
    }

    private sealed class DeterministicCoder(Action<WorkTreeLease> editAction) : ICoder
    {
        public Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            editAction(lease);
            return Task.FromResult(new CoderEditResult(
                Summary: "Implemented requested C# change cleanly inside confined worktree.",
                TouchedFiles: ["code/Calculator.cs"],
                ModelId: "qwen2.5-coder:14b"));
        }
    }

    private (string BareOriginPath, string SourceCheckoutPath, WorkTreeService WorkTree) CreateBareOriginAndWorkTreeService()
    {
        var bareOrigin = Path.Combine(_root, "origin.git");
        var checkout = Path.Combine(_root, "source-repo");
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(bareOrigin);
        Directory.CreateDirectory(checkout);
        Directory.CreateDirectory(home);

        RunGit(bareOrigin, ["init", "--bare", "--initial-branch=main"]);
        RunGit(checkout, ["init", "--initial-branch=main"]);
        RunGit(checkout, ["config", "user.name", "Repo Owner"]);
        RunGit(checkout, ["config", "user.email", "owner@example.test"]);

        Directory.CreateDirectory(Path.Combine(checkout, "code"));
        File.WriteAllText(Path.Combine(checkout, "code", "Calculator.cs"), """
            namespace Demo;

            public static class Calculator
            {
                public static int Add(int a, int b) => a + b;
            }
            """);

        RunGit(checkout, ["add", "."]);
        RunGit(checkout, ["commit", "-m", "Initial commit"]);
        RunGit(checkout, ["remote", "add", "origin", bareOrigin]);
        RunGit(checkout, ["push", "-u", "origin", "main"]);

        var wt = new WorkTreeService(options: new WorkTreeOptions { HomeDirectory = home });
        wt.RegisterRepository(new RegisteredRepository("os", checkout, DefaultBaseBranch: "main"));
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

        var studio = runtime.Get<IInoStudio>(new NeuronId(runtime.Brain, "ino.studio", "studio"));

        InoToolResult proposedResult;
        using (RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value)))
        {
            proposedResult = await studio.ProposeChange("os", "Add Multiply method to Calculator", Ct);
        }

        Assert.True(proposedResult.Ok, proposedResult.Text);
        Assert.NotNull(proposedResult.Card);
        Assert.Equal("approval", proposedResult.Card!.Kind);
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
        var innerBeforeApproval = JsonSerializer.Deserialize<FeatureToPrInner>(headBeforeApproval.InnerJson, FeatureToPrFlow.Json)!;

        Assert.NotNull(innerBeforeApproval.CommitSha);
        Assert.Equal(40, innerBeforeApproval.CommitSha!.Length);
        Assert.EndsWith(":" + innerBeforeApproval.CommitSha, waitingView.ConfirmationPlanId!, StringComparison.Ordinal);
        Assert.Equal(["code/Calculator.cs"], innerBeforeApproval.ChangedFiles);
        Assert.Contains("+    public static int Multiply", innerBeforeApproval.HostRenderedDiff, StringComparison.Ordinal);
        Assert.Null(innerBeforeApproval.PushedRef);

        // Verify Wave 0 B05 hardening: a neuron/agent caller attempting to Resolve a RequiresPerson=true plan
        // is rejected with UnauthorizedAccessException and leaves the human's plan pending & untouched!
        var resolver = runtime.Get<IntoChat.OS.Modules.Apps.Tests.Integration.Approvals.IPlanResolver>(
            new NeuronId(runtime.Brain, IntoChat.OS.Modules.Apps.Tests.Integration.Approvals.PlanResolver.Alias, "resolver"));
        using (RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                resolver.Resolve(waitingView.ConfirmationPlanId!, approve: true));
        }

        // An agent's cancel of the waiting run is refused: the run still waits and its tree stays.
        using (RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value, CallerKind.Agent, conversationId: "ino/conversations/main")))
        {
            var refused = await Assert.ThrowsAsync<DigitalBrain.Rules.RuleViolationException>(() => flows.Cancel(flowId, "agent says stop", Ct));
            Assert.Equal(["apps.flows-person-cancels-approval-wait"], refused.Rejected.Rules);
            Assert.Equal($"flow {flowId} waits for a person at approval-gate: only a person cancels it", refused.Message);
        }
        Assert.Equal(FlowStatus.Waiting, (await starter.Head()).Status);
        Assert.Empty(await runtime.Signals<FlowDone>(FlowTypeFacts.Id(flowId), Ct));
        Assert.True(Directory.Exists(innerBeforeApproval.Lease!.TreePath));

        // Before Person approval, remote bare repo MUST NOT have the branch and flow MUST still be Waiting!
        var branchesBefore = RunGit(bareOrigin, ["branch", "--list", "ino/*"]).Trim();
        Assert.Equal("", branchesBefore);
        Assert.Equal(FlowStatus.Waiting, (await flows.Read(flowId, Ct))!.Status);

        // Approve as real human Person through the trusted runtime edge
        await runtime.ApproveAsPerson(waitingView.ConfirmationPlanId!, Ct);

        var completedView = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status == FlowStatus.Completed);

        Assert.Equal(FeatureToPrFlow.Names, completedView.Done);

        var headAfter = await starter.Head();
        var innerAfter = JsonSerializer.Deserialize<FeatureToPrInner>(headAfter.InnerJson, FeatureToPrFlow.Json)!;

        Assert.NotNull(innerAfter.ApprovedBy);
        Assert.Equal($"refs/heads/{innerAfter.Lease!.BranchName}", innerAfter.PushedRef);
        Assert.False(Directory.Exists(innerAfter.Lease.TreePath));

        // Verify bare remote received the exact approved SHA!
        var pushedSha = RunGit(bareOrigin, ["rev-parse", innerAfter.PushedRef!]).Trim();
        Assert.Equal(innerBeforeApproval.CommitSha, pushedSha);
    }

    [Fact]
    public async Task StartFromJsonSanitizesPrePopulatedCommitAndApprovalFields()
    {
        var (bareOrigin, _, workTree) = CreateBareOriginAndWorkTreeService();
        var coder = new DeterministicCoder(lease =>
        {
            File.WriteAllText(Path.Combine(lease.TreePath, "code", "Calculator.cs"), """
                namespace Demo;
                public static class Calculator { public static int Sub(int a, int b) => a - b; }
                """);
        });

        await using var fixture = new FeatureToPrFixture(workTree, coder);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);

        // Malicious caller tries to smuggle pre-populated ApprovedBy / CommitSha / PushedRef in StartFromJson
        var forgedJson = JsonSerializer.Serialize(new FeatureToPrInner
        {
            RunId = "forgedrun",
            Repository = "os",
            Goal = "Add Sub helper",
            CommitSha = new string('f', 40),
            ApprovedBy = "spoofed-human",
            PushedRef = "refs/heads/ino/spoofed",
        }, FeatureToPrFlow.Json);

        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var flowId = await flows.Start(FeatureToPrFlow.Alias, "Forged attempt", null, forgedJson, Ct);

        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status == FlowStatus.Waiting && v.WaitingStep == "approval-gate");

        var head = await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId)).Head();
        var inner = JsonSerializer.Deserialize<FeatureToPrInner>(head.InnerJson, FeatureToPrFlow.Json)!;

        Assert.NotEqual(new string('f', 40), inner.CommitSha);
        Assert.Null(inner.ApprovedBy);
        Assert.Null(inner.PushedRef);
        Assert.Equal("", RunGit(bareOrigin, ["branch", "--list", "ino/*"]).Trim());

        await flows.Cancel(flowId, "done testing sanitization", Ct);
    }

    private static string RunGit(string workingDirectory, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("git")
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

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}\n{stdout}");
        }

        return stdout;
    }
}
