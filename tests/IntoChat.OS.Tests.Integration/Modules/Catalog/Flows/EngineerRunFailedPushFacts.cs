using System.Text.Json;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Tests;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// A person approved a commit and the push then failed (the remote could not be reached): what is left of the approved work.
public sealed class EngineerRunFailedPushFacts : IDisposable
{
    private readonly EngineerRunsKit _kit = new("eng-push");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _kit.Dispose();

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task An_approved_commit_survives_a_failed_push(NeuronRuntimeKind kind)
    {
        var (bareOrigin, checkout, realWorkTree) = _kit.CreateRepository("push-" + kind);
        var workTree = new EngineerRunsKit.FaultingWorkTree(realWorkTree);
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), new EngineerRunsKit.ScriptedLiveVerifier());
        await using var runtime = await fixture.Start(kind, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "push01"), Ct);
        Assert.True(started.Ok, started.SummaryText);
        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);
        Assert.True(waiting is { Status: FlowStatus.Waiting, WaitingStep: "approval-gate" }, waiting.Detail);

        var starter = runtime.Get<IFlowStarter>(FlowTypeFacts.Id(started.FlowId));
        var approved = JsonSerializer.Deserialize<EngineerRun>((await starter.Head()).InnerJson, EngineerFlow.Json)!.SelectedInstance!;
        var sha = approved.Commit!.Commit.Value;
        var treePath = approved.Lease!.TreePath;
        Assert.True(Directory.Exists(treePath));

        workTree.FailPush = true;
        await runtime.ApproveAsPerson(waiting.ConfirmationPlanId!, Ct);

        var failed = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v.Status is FlowStatus.Failed or FlowStatus.Completed or FlowStatus.Cancelled);
        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.Contains("unable to access the remote", failed.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, workTree.PushAttempts);
        Assert.DoesNotContain("push", failed.Done);
        Assert.Equal("", EngineerRunsKit.Git(bareOrigin, "branch", "--list", "ino/*").Trim());

        // The approved commit survives: it is still on its branch in the source repository, so nothing a person approved is lost.
        Assert.Equal(sha, EngineerRunsKit.Git(checkout, "rev-parse", approved.Lease.BranchName).Trim());
        Assert.Equal("commit", EngineerRunsKit.Git(checkout, "cat-file", "-t", sha).Trim());

        // What does not survive is the worktree: a failed step removes every worktree of the run, and a failed flow does not run
        // again. The branch has to be pushed by hand, or the change run again.
        Assert.False(Directory.Exists(treePath));

        // The run says so: it failed at push, with the approval and the commit still recorded.
        var after = JsonSerializer.Deserialize<EngineerRun>((await starter.Head()).InnerJson, EngineerFlow.Json)!;
        Assert.Equal(sha, after.SelectedInstance!.Commit!.Commit.Value);
        Assert.NotNull(after.Approval);
        Assert.Null(after.Publication?.PushedRef);
    }
}
