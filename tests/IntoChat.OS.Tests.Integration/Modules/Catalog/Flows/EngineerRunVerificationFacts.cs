using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Tests;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// The verification policy of flow.engineer as a run shows it: every gate runs unless a person switched one off at the start,
// a switched-off gate is a skipped step the run records and names wherever its status is shown, and nobody but a person or
// the kernel may switch one off.
public sealed class EngineerRunVerificationFacts : IDisposable
{
    private static readonly EngineerVerificationPolicy NoGates = new(BuildAndTest: false, LiveVerify: false);

    private readonly EngineerRunsKit _kit = new("eng-verify");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _kit.Dispose();

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task A_run_passes_every_gate_unless_told_otherwise(NeuronRuntimeKind kind)
    {
        var (_, _, workTree) = _kit.CreateRepository("full-" + kind);
        var build = new EngineerRunsKit.CountingBuildVerifier();
        var live = new EngineerRunsKit.ScriptedLiveVerifier();
        await using var fixture = new EngineerRunsKit.Fixture(workTree, build, live);
        await using var runtime = await fixture.Start(kind, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "full01"), Ct);
        Assert.True(started.Ok, started.SummaryText);
        var waiting = await AtApprovalGate(flows, started.FlowId);

        Assert.Equal(1, build.Calls);
        Assert.Equal(1, live.Calls);
        var summary = await runs.InspectEngineerRun(started.FlowId, Ct);
        Assert.Equal(EngineerVerificationPolicy.Full, summary.Verification);
        Assert.DoesNotContain("SKIPPED", summary.SummaryText, StringComparison.Ordinal);
        Assert.DoesNotContain("SKIPPED", (await Plan(runtime, waiting.ConfirmationPlanId!)).Body, StringComparison.Ordinal);
        Assert.DoesNotContain(await runtime.Signals<FlowStep>(FlowTypeFacts.Id(started.FlowId), Ct), static step => step.Outcome == StepOutcome.Skipped);

        await flows.Cancel(started.FlowId, "test cleanup", Ct);
    }

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task A_person_switches_the_gates_off_and_the_run_says_so_everywhere(NeuronRuntimeKind kind)
    {
        var (bareOrigin, _, workTree) = _kit.CreateRepository("skip-" + kind);
        var build = new EngineerRunsKit.CountingBuildVerifier();
        var live = new EngineerRunsKit.ScriptedLiveVerifier();
        await using var fixture = new EngineerRunsKit.Fixture(workTree, build, live);
        await using var runtime = await fixture.Start(kind, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(
            new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "skip01", Verification: NoGates with { SkippedBy = "someone-else" }),
            Ct);
        Assert.True(started.Ok, started.SummaryText);
        var waiting = await AtApprovalGate(flows, started.FlowId);

        // Neither gate ran, and both are done as skipped steps with the reason.
        Assert.Equal(0, build.Calls);
        Assert.Equal(0, live.Calls);
        var skipped = (await runtime.Signals<FlowStep>(FlowTypeFacts.Id(started.FlowId), Ct)).Where(static step => step.Outcome == StepOutcome.Skipped).ToArray();
        Assert.Equal(["build-and-test", "live-verify"], skipped.Select(static step => step.Step));
        Assert.All(skipped, static step => Assert.Contains("switched off by person", step.Detail, StringComparison.Ordinal));

        // The run keeps the policy and who switched the gates off: the caller, not the name the payload gave.
        var run = await Run(runtime, started.FlowId);
        Assert.Equal(new EngineerVerificationPolicy(false, false, "person"), run.Verification);
        Assert.Null(run.SelectedInstance!.Build);
        Assert.Null(run.SelectedInstance.Live);

        // The summary, Ino's card and the approval a person reads all name the skip.
        const string line = "Verification SKIPPED for build-and-test and live-verify by person";
        var summary = await runs.InspectEngineerRun(started.FlowId, Ct);
        Assert.Equal(run.Verification, summary.Verification);
        Assert.Contains(line, summary.SummaryText, StringComparison.Ordinal);
        Assert.Contains(line, summary.Card!.Summary, StringComparison.Ordinal);
        Assert.Contains(summary.DiagnosticsAndTraces, trace => trace.Contains(line, StringComparison.Ordinal));
        Assert.StartsWith(line, (await Plan(runtime, waiting.ConfirmationPlanId!)).Body, StringComparison.Ordinal);

        // A person still approves the exact commit, and it is pushed.
        await runtime.ApproveAsPerson(waiting.ConfirmationPlanId!, Ct);
        var completed = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v.Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled);
        Assert.True(completed.Status == FlowStatus.Completed, completed.Detail);
        Assert.Equal(EngineerFlow.Names, completed.Done);
        var after = await Run(runtime, started.FlowId);
        Assert.Equal(after.SelectedInstance!.Commit!.Commit.Value, EngineerRunsKit.Git(bareOrigin, "rev-parse", after.Publication!.PushedRef!).Trim());
        Assert.Contains(line, (await runs.InspectEngineerRun(started.FlowId, Ct)).SummaryText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task One_gate_can_be_switched_off_and_the_other_still_runs(NeuronRuntimeKind kind)
    {
        var (_, _, workTree) = _kit.CreateRepository("partial-" + kind);
        var build = new EngineerRunsKit.CountingBuildVerifier();
        var live = new EngineerRunsKit.ScriptedLiveVerifier();
        await using var fixture = new EngineerRunsKit.Fixture(workTree, build, live);
        await using var runtime = await fixture.Start(kind, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(
            new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "partial01", Verification: new EngineerVerificationPolicy(BuildAndTest: false)),
            Ct);
        Assert.True(started.Ok, started.SummaryText);
        await AtApprovalGate(flows, started.FlowId);

        Assert.Equal(0, build.Calls);
        Assert.Equal(1, live.Calls);
        var run = await Run(runtime, started.FlowId);
        Assert.Null(run.SelectedInstance!.Build);
        Assert.True(run.SelectedInstance.Live?.Succeeded);
        Assert.Equal(["build-and-test"], run.Verification.SkippedGates);

        await flows.Cancel(started.FlowId, "test cleanup", Ct);
    }

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS, Who.Agent)]
    [InlineData(NeuronRuntimeKind.TestOS, Who.App)]
    [InlineData(NeuronRuntimeKind.Silo, Who.Agent)]
    [InlineData(NeuronRuntimeKind.Silo, Who.App)]
    public async Task Only_a_person_switches_a_gate_off(NeuronRuntimeKind kind, Who who)
    {
        var (_, _, workTree) = _kit.CreateRepository($"refuse-{kind}-{who}");
        var build = new EngineerRunsKit.CountingBuildVerifier();
        await using var fixture = new EngineerRunsKit.Fixture(workTree, build, new EngineerRunsKit.ScriptedLiveVerifier());
        await using var runtime = await fixture.Start(kind, Ct);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var flowId = new NeuronId(runtime.Brain, IEngineerRuns.EngineerFlow, "refuse01").ToString();

        using (runtime.As(who, conversation: who == Who.Agent ? "ino/conversations/main" : null))
        {
            // Through the door: the run is not started and the answer says why.
            var refused = await runs.StartEngineerRun(
                new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "refuse01", Verification: new EngineerVerificationPolicy(LiveVerify: false)),
                Ct);
            Assert.False(refused.Ok);
            Assert.Contains("only a person or the kernel switches a verification gate off", refused.SummaryText, StringComparison.Ordinal);

            // Straight through the flows index with a hand-written payload: refused the same way.
            var payload = """{"runId":"refuse02","repository":"os","goal":"Add Multiply","verification":{"buildAndTest":false,"skippedBy":"person"}}""";
            var error = await Assert.ThrowsAnyAsync<Exception>(() => flows.Start(IEngineerRuns.EngineerFlow, "Skip", null, payload, Ct, flowId: "refuse02"));
            Assert.Contains("only a person or the kernel switches a verification gate off", error.ToString(), StringComparison.Ordinal);
        }

        using var person = runtime.As(Who.Person);
        Assert.Equal(FlowStatus.Pending, (await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId)).Head()).Status);
        Assert.Equal(0, build.Calls);

        // The same caller starts the same run with every gate on.
        EngineerRunSummary allowed;
        using (runtime.As(who, conversation: who == Who.Agent ? "ino/conversations/main" : null))
        {
            allowed = await runs.StartEngineerRun(new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "allowed01"), Ct);
        }

        Assert.True(allowed.Ok, allowed.SummaryText);
        await AtApprovalGate(flows, allowed.FlowId);
        Assert.Equal(1, build.Calls);
        await flows.Cancel(allowed.FlowId, "test cleanup", Ct);
    }

    private static async Task<FlowView> AtApprovalGate(IFlows flows, string flowId)
    {
        var view = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);
        Assert.True(view is { Status: FlowStatus.Waiting, WaitingStep: "approval-gate" }, $"{view.Status} at {view.WaitingStep ?? view.Current}: {view.Detail}");
        return view;
    }

    private static async Task<EngineerRun> Run(INeuronRuntime runtime, string flowId)
        => JsonSerializer.Deserialize<EngineerRun>((await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId)).Head()).InnerJson, EngineerFlow.Json)!;

    private static async Task<ConfirmationPlan> Plan(INeuronRuntime runtime, string planId)
    {
        var inbox = runtime.Get<IConfirmationInbox>(new NeuronId(runtime.Brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
        return Assert.Single((await inbox.Read()).Pending, pending => pending.PlanId == planId).Plan;
    }
}
