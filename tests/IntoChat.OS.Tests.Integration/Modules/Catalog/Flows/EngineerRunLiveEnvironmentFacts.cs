using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Tests;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// live-verify needs a running system to check the change against. Where there is none, the run does not skip the gate and does
// not throw its work away: it waits at live-verify, says why, and checks again when it is resumed or its wait runs out.
public sealed class EngineerRunLiveEnvironmentFacts : IDisposable
{
    private readonly EngineerRunsKit _kit = new("eng-live");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _kit.Dispose();

    // The product's own verifier with nothing configured: no candidate host and no live endpoint. Its answer is the real one.
    private static EngineerLiveReport NoEnvironment()
    {
        using var verifier = new EngineerLiveVerifier();
        return verifier.VerifyLiveAsync(Path.GetTempPath(), "goal", ["code/Calculator.cs"], Ct).GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task Without_a_live_environment_the_run_waits_at_live_verify_and_goes_on_when_resumed_with_one(NeuronRuntimeKind kind)
    {
        var (_, _, workTree) = _kit.CreateRepository("wait-" + kind);
        var build = new EngineerRunsKit.CountingBuildVerifier();
        var live = new EngineerRunsKit.ScriptedLiveVerifier { Answer = NoEnvironment };
        await using var fixture = new EngineerRunsKit.Fixture(workTree, build, live);
        await using var runtime = await fixture.Start(kind, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "live01"), Ct);
        Assert.True(started.Ok, started.SummaryText);

        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);

        // Visible: the run waits at live-verify and says why. Nothing was skipped and nobody was asked to approve.
        Assert.True(waiting is { Status: FlowStatus.Waiting, WaitingStep: "live-verify", WaitingFor: WaitReason.Schedule }, $"{waiting.Status} at {waiting.WaitingStep}: {waiting.Detail}");
        Assert.Contains("no live environment", waiting.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("live-verify", waiting.Done);
        Assert.Equal(1, live.Calls);
        Assert.Empty((await Inbox(runtime).Read()).Pending);

        // Nothing is lost while it waits: the worktree and the commit are there, and the candidate is not marked failed.
        var parked = await Run(runtime, started.FlowId);
        var candidate = parked.SelectedInstance!;
        Assert.True(Directory.Exists(candidate.Lease!.TreePath));
        Assert.NotNull(candidate.Commit);
        Assert.True(candidate.Build?.Succeeded);
        Assert.Null(candidate.Live);
        Assert.Equal("built", candidate.Status);
        Assert.Null(candidate.FailureDetail);

        var summary = await runs.InspectEngineerRun(started.FlowId, Ct);
        Assert.True(summary.Ok);
        Assert.Equal("live-verify", summary.CurrentStage);
        Assert.Contains("no live environment", summary.SummaryText, StringComparison.Ordinal);

        // Resumed while there is still no environment, it checks again and waits again: the gate is never passed unchecked.
        await flows.Resume(started.FlowId, "live-verify", WaitOutcome.Arrived, Ct);
        await FlowTypeFacts.Eventually(() => flows.Read(started.FlowId, Ct), _ => live.Calls >= 2);
        var again = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);
        Assert.True(again is { Status: FlowStatus.Waiting, WaitingStep: "live-verify" }, $"{again.Status} at {again.WaitingStep}: {again.Detail}");
        Assert.DoesNotContain("live-verify", again.Done);

        // Resumable: once an environment answers, a resume runs the check and the run goes on to the approval gate.
        live.Answer = EngineerRunsKit.ScriptedLiveVerifier.Green;
        await flows.Resume(started.FlowId, "live-verify", WaitOutcome.Arrived, Ct);
        var atGate = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v is { Status: FlowStatus.Waiting, WaitingStep: "approval-gate" } or { Status: FlowStatus.Failed or FlowStatus.Completed });
        Assert.True(atGate.Status == FlowStatus.Waiting, atGate.Detail);
        Assert.Contains("live-verify", atGate.Done);
        Assert.Equal(3, live.Calls);
        Assert.Equal(1, build.Calls);
        Assert.True((await Run(runtime, started.FlowId)).SelectedInstance!.Live?.Succeeded);

        await flows.Cancel(started.FlowId, "test cleanup", Ct);
    }

    [Fact]
    public async Task The_wait_runs_out_and_the_run_checks_again_by_itself()
    {
        var (_, _, workTree) = _kit.CreateRepository("retry");
        var live = new EngineerRunsKit.ScriptedLiveVerifier { Answer = NoEnvironment };
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), live);
        await using var runtime = await fixture.Start(NeuronRuntimeKind.TestOS, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "live02"), Ct);
        await FlowTypeFacts.Eventually(() => flows.Read(started.FlowId, Ct), v => v is { Status: FlowStatus.Waiting, WaitingStep: "live-verify" });
        Assert.Equal(1, live.Calls);

        live.Answer = EngineerRunsKit.ScriptedLiveVerifier.Green;
        await runtime.Elapse(EngineerFlow.LiveEnvironmentRecheck + TimeSpan.FromMinutes(1), Ct);

        var atGate = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v is { Status: FlowStatus.Waiting, WaitingStep: "approval-gate" } or { Status: FlowStatus.Failed or FlowStatus.Completed });
        Assert.True(atGate.Status == FlowStatus.Waiting, atGate.Detail);
        Assert.Equal(2, live.Calls);

        await flows.Cancel(started.FlowId, "test cleanup", Ct);
    }

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task A_live_check_that_ran_and_failed_still_fails_the_run(NeuronRuntimeKind kind)
    {
        var (_, _, workTree) = _kit.CreateRepository("fail-" + kind);
        var live = new EngineerRunsKit.ScriptedLiveVerifier
        {
            Answer = static () => new EngineerLiveReport(false, true, false, true, 0, 1, 0, 0,
                [new EngineerLiveProbe("mcp-behavioral-probe", "mcp", false, 500, "The changed app answered 500.")], [], "LiveVerify=False · MCP probe failed."),
        };
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), live);
        await using var runtime = await fixture.Start(kind, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "live03"), Ct);
        var failed = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v.Status is FlowStatus.Failed or FlowStatus.Completed || v is { Status: FlowStatus.Waiting, WaitingStep: not null });

        Assert.Equal(FlowStatus.Failed, failed.Status);
        Assert.Contains("MCP probe failed", failed.Detail, StringComparison.Ordinal);
        Assert.Equal(1, live.Calls);
    }

    private static async Task<EngineerRun> Run(INeuronRuntime runtime, string flowId)
        => JsonSerializer.Deserialize<EngineerRun>((await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId)).Head()).InnerJson, EngineerFlow.Json)!;

    private static IConfirmationInbox Inbox(INeuronRuntime runtime)
        => runtime.Get<IConfirmationInbox>(new NeuronId(runtime.Brain, "runtime.confirmations", IConfirmations.ConfirmationsNeuronKey));
}
