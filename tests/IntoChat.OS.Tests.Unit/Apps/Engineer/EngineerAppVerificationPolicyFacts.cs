using DigitalBrain.Flows;
using IntoChat.OS.Apps.Engineer;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Apps.Tests.Unit.Engineer;

// What the Engineer workbench shows of a run's verification policy: a gate that was switched off is named on the banner and is
// never counted as verification that passed.
public sealed class EngineerAppVerificationPolicyFacts
{
    private static EngineerRunSummary AtApprovalGate(EngineerVerificationPolicy? verification)
        => new(
            Ok: true,
            FlowId: "owner/flow.engineer/run1",
            RunId: "run1",
            RepoSlug: "os",
            Goal: "Add Multiply",
            Backend: "LocalGit",
            ParallelInstances: 1,
            Status: FlowStatus.Waiting,
            CurrentStage: "approval-gate",
            CompletedStages: ["brainstorm-and-plan", "prepare-worktree", "implement-tasks", "build-and-test", "live-verify"],
            ConfirmationPlanId: "app:engineer:run1:" + new string('d', 40),
            TraceId: null,
            WinningCommitSha: new string('d', 40),
            PushedRef: null,
            PullRequestUrl: null,
            EngineerAppUri: "neuron://owner/apps/engineer#owner/flow.engineer/run1",
            TasksBoardUri: "neuron://owner/apps/tasks#owner/flow.engineer/run1",
            Plan: null,
            Instances: [],
            DiagnosticsAndTraces: [],
            SummaryText: "waiting",
            Verification: verification);

    [Fact]
    public void A_run_that_passed_every_gate_shows_verification_passed()
    {
        var state = EngineerApp.ApplyLiveSummary(new EngineerWorkspaceState(), AtApprovalGate(EngineerVerificationPolicy.Full), preserveActiveTab: false, preferredFilePath: null);

        Assert.True(state.VerificationPassed);
        Assert.DoesNotContain("SKIPPED", state.StatusBanner, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_with_a_gate_switched_off_says_so_and_does_not_show_verification_passed()
    {
        var policy = new EngineerVerificationPolicy(BuildAndTest: true, LiveVerify: false, SkippedBy: "ada");

        var state = EngineerApp.ApplyLiveSummary(new EngineerWorkspaceState(), AtApprovalGate(policy), preserveActiveTab: false, preferredFilePath: null);

        Assert.False(state.VerificationPassed);
        Assert.Contains("Verification SKIPPED for live-verify by ada", state.StatusBanner, StringComparison.Ordinal);
    }
}
