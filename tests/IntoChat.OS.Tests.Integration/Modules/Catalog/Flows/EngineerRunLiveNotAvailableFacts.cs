using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Tests;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// An installation with no live environment configured cannot live-verify at all. A run there does not wait for ever: it
// records live-verify as not available, which is neither a pass nor a person's skip, and goes on to the approval, where the
// person reads first that the change was not live-verified. The installation decides this, never the caller.
public sealed class EngineerRunLiveNotAvailableFacts : IDisposable
{
    private const string Line = "NOT LIVE-VERIFIED: no live environment is available on this installation";

    private readonly EngineerRunsKit _kit = new("eng-nolive");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _kit.Dispose();

    // The answer the product's own verifier gives when it is asked with nothing to check against.
    private static EngineerLiveReport NoEnvironment()
    {
        using var verifier = new EngineerLiveVerifier();
        return verifier.VerifyLiveAsync(Path.GetTempPath(), "goal", ["code/Calculator.cs"], Ct).GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task With_no_live_environment_configured_the_run_records_not_available_and_a_person_approves_knowingly(NeuronRuntimeKind kind)
    {
        var (bareOrigin, _, workTree) = _kit.CreateRepository("na-" + kind);
        var build = new EngineerRunsKit.CountingBuildVerifier();
        // The product's own verifier, as the catalog registers it: no candidate verifier, no live endpoint.
        await using var fixture = new EngineerRunsKit.Fixture(workTree, build, liveVerifier: null);
        await using var runtime = await fixture.Start(kind, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "na01"), Ct);
        Assert.True(started.Ok, started.SummaryText);
        var waiting = await AtApprovalGate(flows, started.FlowId);

        // Build-and-test ran and gated. live-verify did not run: it is done as a skipped step that says why.
        Assert.Equal(1, build.Calls);
        var skipped = Assert.Single(await runtime.Signals<FlowStep>(FlowTypeFacts.Id(started.FlowId), Ct), static step => step.Outcome == StepOutcome.Skipped);
        Assert.Equal("live-verify", skipped.Step);
        Assert.Equal(EngineerFlow.LiveVerifyNotAvailableReason, skipped.Detail);
        Assert.Contains("NOT live-verified", skipped.Detail, StringComparison.Ordinal);

        // The run keeps the outcome. It is not a pass (no live report) and not a person's skip (the gate is on, nobody skipped).
        var run = await Run(runtime, started.FlowId);
        Assert.Equal(new EngineerVerificationPolicy(BuildAndTest: true, LiveVerify: true, SkippedBy: null, LiveVerifyNotAvailable: true), run.Verification);
        Assert.False(run.Verification.SkipsAny);
        Assert.True(run.SelectedInstance!.Build?.Succeeded);
        Assert.Null(run.SelectedInstance.Live);

        // Said wherever the run is shown: the summary, the text the model gets, Ino's card, the diagnostics the workbench
        // carries, and first on the approval the person reads.
        var summary = await runs.InspectEngineerRun(started.FlowId, Ct);
        Assert.Equal(run.Verification, summary.Verification);
        Assert.Contains(Line, summary.SummaryText, StringComparison.Ordinal);
        Assert.Contains(Line, summary.Card!.Summary, StringComparison.Ordinal);
        Assert.Contains(summary.DiagnosticsAndTraces, trace => trace.Contains(Line, StringComparison.Ordinal));
        Assert.DoesNotContain("SKIPPED", summary.SummaryText, StringComparison.Ordinal);
        Assert.StartsWith(Line, (await Plan(runtime, waiting.ConfirmationPlanId!)).Body, StringComparison.Ordinal);

        // A person still approves the exact commit, and it is pushed.
        await runtime.ApproveAsPerson(waiting.ConfirmationPlanId!, Ct);
        var completed = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v.Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled);
        Assert.True(completed.Status == FlowStatus.Completed, completed.Detail);
        var after = await Run(runtime, started.FlowId);
        Assert.Equal(after.SelectedInstance!.Commit!.Commit.Value, EngineerRunsKit.Git(bareOrigin, "rev-parse", after.Publication!.PushedRef!).Trim());
        Assert.True(after.Verification.LiveVerifyNotAvailable);
        Assert.Contains(Line, (await runs.InspectEngineerRun(started.FlowId, Ct)).SummaryText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task Build_and_test_still_gates_a_run_that_cannot_be_live_verified(NeuronRuntimeKind kind)
    {
        var (_, _, workTree) = _kit.CreateRepository("nabuild-" + kind);
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new FailingBuildVerifier(), liveVerifier: null);
        await using var runtime = await fixture.Start(kind, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "na02"), Ct);
        var ended = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v.Status is FlowStatus.Failed or FlowStatus.Completed || v is { Status: FlowStatus.Waiting, WaitingStep: not null });

        Assert.Equal(FlowStatus.Failed, ended.Status);
        Assert.DoesNotContain("live-verify", ended.Done);
        Assert.Empty((await Inbox(runtime).Read()).Pending);
    }

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS, Who.Person)]
    [InlineData(NeuronRuntimeKind.TestOS, Who.Agent)]
    [InlineData(NeuronRuntimeKind.TestOS, Who.App)]
    [InlineData(NeuronRuntimeKind.Silo, Who.Person)]
    [InlineData(NeuronRuntimeKind.Silo, Who.Agent)]
    [InlineData(NeuronRuntimeKind.Silo, Who.App)]
    public async Task No_caller_produces_not_available_by_argument(NeuronRuntimeKind kind, Who who)
    {
        var (_, _, workTree) = _kit.CreateRepository($"arg-{kind}-{who}");
        // An environment is configured and answers: live-verify must run whatever the caller writes.
        var live = new EngineerRunsKit.EnvironmentLiveVerifier();
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), live);
        await using var runtime = await fixture.Start(kind, Ct);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var conversation = who == Who.Agent ? "ino/conversations/main" : null;

        // Through the door, with the outcome written into the request.
        EngineerRunSummary viaDoor;
        using (runtime.As(who, conversation: conversation))
        {
            viaDoor = await runs.StartEngineerRun(
                new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "arg01", Verification: new EngineerVerificationPolicy(LiveVerifyNotAvailable: true)),
                Ct);
        }

        Assert.True(viaDoor.Ok, viaDoor.SummaryText);

        // Straight through the flows index with a hand-written payload.
        string viaIndex;
        using (runtime.As(who, conversation: conversation))
        {
            const string payload = """{"runId":"arg02","repository":"os","goal":"Add Multiply to Calculator","verification":{"buildAndTest":true,"liveVerify":true,"liveVerifyNotAvailable":true}}""";
            viaIndex = await flows.Start(IEngineerRuns.EngineerFlow, "Forged", null, payload, Ct, flowId: "arg02");
        }

        using var person = runtime.As(Who.Person);
        foreach (var flowId in (string[])[viaDoor.FlowId, viaIndex])
        {
            await AtApprovalGate(flows, flowId);
            var run = await Run(runtime, flowId);
            Assert.Equal(EngineerVerificationPolicy.Full, run.Verification);
            Assert.True(run.SelectedInstance!.Live?.Succeeded);
            Assert.DoesNotContain(await runtime.Signals<FlowStep>(FlowTypeFacts.Id(flowId), Ct), static step => step.Outcome == StepOutcome.Skipped);
            await flows.Cancel(flowId, "test cleanup", Ct);
        }

        Assert.Equal(2, live.Calls);
    }

    // A brain holds a run that an earlier version parked at live-verify, because the product's verifier answered that it had
    // nothing to check against and the run waited. The brain restarts on this version, where the same installation is known to
    // have no live environment: the run moves on at its next resume, without asking the verifier again.
    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task A_run_parked_at_live_verify_moves_on_after_a_restart_when_it_is_resumed(NeuronRuntimeKind kind)
    {
        var (_, _, workTree) = _kit.CreateRepository("parked-" + kind);
        // As the earlier version saw it: the check is tried, and the answer is that there is no environment.
        var live = new EngineerRunsKit.EnvironmentLiveVerifier { Configured = true, Answer = NoEnvironment };
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), live);
        await using var runtime = await fixture.Start(kind, Ct, home: Path.Combine(_kit.Root, "home-parked-" + kind));
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "parked01"), Ct);
        var parked = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v is { Status: FlowStatus.Waiting, Detail.Length: > 0 } or { Status: FlowStatus.Failed or FlowStatus.Completed });
        Assert.True(parked is { Status: FlowStatus.Waiting, WaitingStep: "live-verify" }, $"{parked.Status} at {parked.WaitingStep}: {parked.Detail}");
        Assert.Equal(1, live.Calls);
        // What is stored for the parked run is what the earlier version stored: no outcome is written.
        Assert.DoesNotContain("liveVerifyNotAvailable", (await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(started.FlowId)).Head()).InnerJson, StringComparison.Ordinal);

        await runtime.Restart(Ct);
        live.Configured = false;
        flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        Assert.Equal(FlowStatus.Waiting, (await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(started.FlowId)).Head()).Status);

        await flows.Resume(started.FlowId, "live-verify", WaitOutcome.Arrived, Ct);

        var atGate = await AtApprovalGate(flows, started.FlowId);
        Assert.Contains("live-verify", atGate.Done);
        Assert.Equal(1, live.Calls);
        var run = await Run(runtime, started.FlowId);
        Assert.True(run.Verification.LiveVerifyNotAvailable);
        Assert.Null(run.Verification.SkippedBy);
        Assert.Null(run.SelectedInstance!.Live);
        Assert.StartsWith(Line, (await Plan(runtime, atGate.ConfirmationPlanId!)).Body, StringComparison.Ordinal);

        await flows.Cancel(started.FlowId, "test cleanup", Ct);
    }

    // The same, with nobody resuming it: the parked run's own 30-minute recheck finds the rule. No restart here: the in-memory
    // runtime keeps reminders with the activation and loses them on a restart, where a silo keeps them, and a silo cannot be
    // made to wait 30 minutes in a test. The restart is covered by the fact above, the recheck by this one.
    [Fact]
    public async Task A_run_parked_at_live_verify_moves_on_at_its_next_recheck()
    {
        var (_, _, workTree) = _kit.CreateRepository("parked-recheck");
        var live = new EngineerRunsKit.EnvironmentLiveVerifier { Configured = true, Answer = NoEnvironment };
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), live);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.TestOS, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "parked02"), Ct);
        await FlowTypeFacts.Eventually(() => flows.Read(started.FlowId, Ct), v => v is { Status: FlowStatus.Waiting, WaitingStep: "live-verify" });

        live.Configured = false;
        await runtime.Elapse(EngineerFlow.LiveEnvironmentRecheck + TimeSpan.FromMinutes(1), Ct);

        await AtApprovalGate(flows, started.FlowId);
        Assert.Equal(1, live.Calls);
        Assert.True((await Run(runtime, started.FlowId)).Verification.LiveVerifyNotAvailable);

        await flows.Cancel(started.FlowId, "test cleanup", Ct);
    }

    // An environment that is configured but cannot be reached is not "not available": the run waits, as before.
    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task A_configured_environment_that_cannot_be_reached_still_makes_the_run_wait(NeuronRuntimeKind kind)
    {
        var (_, _, workTree) = _kit.CreateRepository("unreach-" + kind);
        var live = new EngineerRunsKit.EnvironmentLiveVerifier { Configured = true, Answer = NoEnvironment };
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), live);
        await using var runtime = await fixture.Start(kind, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "unreach01"), Ct);
        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v is { Status: FlowStatus.Waiting, Detail.Length: > 0 } or { Status: FlowStatus.Failed or FlowStatus.Completed });

        Assert.True(waiting is { Status: FlowStatus.Waiting, WaitingStep: "live-verify", WaitingFor: WaitReason.Schedule }, $"{waiting.Status} at {waiting.WaitingStep}: {waiting.Detail}");
        Assert.DoesNotContain("live-verify", waiting.Done);
        Assert.Equal(1, live.Calls);
        Assert.False((await Run(runtime, started.FlowId)).Verification.LiveVerifyNotAvailable);
        Assert.Empty((await Inbox(runtime).Read()).Pending);

        await flows.Cancel(started.FlowId, "test cleanup", Ct);
    }

    private sealed class FailingBuildVerifier : IEngineerBuildVerifier
    {
        public Task<EngineerBuildReport> VerifyAsync(string worktreePath, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
            => Task.FromResult(new EngineerBuildReport(false, false, true, false, 0, 1, 0, 1, ["Calculator.cs(7,1): error CS1002"], 10, "Build failed."));
    }

    private static async Task<FlowView> AtApprovalGate(IFlows flows, string flowId)
    {
        var view = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v is { Status: FlowStatus.Waiting, WaitingStep: "approval-gate" } or { Status: FlowStatus.Failed or FlowStatus.Completed });
        Assert.True(view is { Status: FlowStatus.Waiting, WaitingStep: "approval-gate" }, $"{view.Status} at {view.WaitingStep ?? view.Current}: {view.Detail}");
        return view;
    }

    private static async Task<EngineerRun> Run(INeuronRuntime runtime, string flowId)
        => JsonSerializer.Deserialize<EngineerRun>((await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId)).Head()).InnerJson, EngineerFlow.Json)!;

    private static IConfirmationInbox Inbox(INeuronRuntime runtime)
        => runtime.Get<IConfirmationInbox>(new NeuronId(runtime.Brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));

    private static async Task<ConfirmationPlan> Plan(INeuronRuntime runtime, string planId)
        => Assert.Single((await Inbox(runtime).Read()).Pending, pending => pending.PlanId == planId).Plan;
}
