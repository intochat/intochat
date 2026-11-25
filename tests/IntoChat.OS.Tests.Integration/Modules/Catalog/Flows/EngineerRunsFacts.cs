using DigitalBrain;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Tests;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// The engineering door (IEngineerRuns): it starts a run through the flows index, reads it, and passes a ratification or a
// candidate choice to the run, carrying the caller on. The flows index itself offers none of these.
public sealed class EngineerRunsFacts : IDisposable
{
    private readonly EngineerRunsKit _kit = new("eng-runs");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _kit.Dispose();

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task An_agent_starts_a_run_through_the_door_and_a_person_promotes_another_candidate(NeuronRuntimeKind kind)
    {
        var (_, _, workTree) = _kit.CreateRepository("door-" + kind);
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), new EngineerRunsKit.ScriptedLiveVerifier());
        await using var runtime = await fixture.Start(kind, Ct);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        EngineerRunSummary started;
        using (runtime.As(Who.Agent, conversation: "ino/conversations/main"))
        {
            started = await runs.StartEngineerRun(
                new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", ParallelInstances: 2, RunId: "door01"),
                Ct);
        }

        Assert.True(started.Ok, started.SummaryText);
        Assert.Equal($"{runtime.Brain.Value}/flow.engineer/door01", started.FlowId);

        using var person = runtime.As(Who.Person);
        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);
        Assert.True(waiting.Status == FlowStatus.Waiting, waiting.Detail);
        Assert.Equal("approval-gate", waiting.WaitingStep);

        // The index recorded the caller that came through the door, not the door.
        Assert.Equal("ino/conversations/main", waiting.Conversation);
        Assert.Equal("person", waiting.Owner);
        Assert.False(string.IsNullOrWhiteSpace(waiting.App));

        // No id reads the brain's latest engineering run.
        var latest = await runs.InspectEngineerRun(null, Ct);
        Assert.Equal(started.FlowId, latest.FlowId);
        Assert.Equal("approval-gate", latest.CurrentStage);
        Assert.Equal(2, latest.Instances.Length);
        Assert.Equal(0, latest.SelectedCandidateIndex);

        var promoted = await runs.SelectEngineerCandidate(started.FlowId, 1, Ct);
        Assert.Equal(1, promoted.SelectedCandidateIndex);
        Assert.EndsWith(":" + promoted.Instances.Single(static i => i.InstanceIndex == 1).CommitSha, promoted.ConfirmationPlanId!, StringComparison.Ordinal);

        await flows.Cancel(started.FlowId, "test cleanup", Ct);
    }

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task A_person_ratifies_the_plan_through_the_door_and_the_run_goes_on(NeuronRuntimeKind kind)
    {
        var (_, _, workTree) = _kit.CreateRepository("ratify-" + kind);
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), new EngineerRunsKit.ScriptedLiveVerifier());
        await using var runtime = await fixture.Start(kind, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        var started = await runs.StartEngineerRun(
            new EngineerDelegationRequest(RepoSlug: "os", Goal: "Add Multiply to Calculator", RunId: "ratify01", RequirePlanRatification: true),
            Ct);
        Assert.True(started.Ok, started.SummaryText);

        var drafted = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);
        Assert.Equal("brainstorm-and-plan", drafted.WaitingStep);
        Assert.True((await runs.InspectEngineerRun(started.FlowId, Ct)).AwaitingPlanRatification);

        var ratified = await runs.RatifyEngineerPlan(started.FlowId, ct: Ct);
        Assert.Equal("person", ratified.RatifiedPlan?.RatifiedBy);

        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(started.FlowId, Ct),
            v => v is { Status: FlowStatus.Waiting, WaitingStep: "approval-gate" } or { Status: FlowStatus.Failed or FlowStatus.Completed });
        Assert.True(waiting.Status == FlowStatus.Waiting, waiting.Detail);

        await flows.Cancel(started.FlowId, "test cleanup", Ct);
    }

    [Fact]
    public async Task The_door_refuses_a_run_of_another_brain_and_reports_a_brain_without_runs()
    {
        var (_, _, workTree) = _kit.CreateRepository("none");
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), new EngineerRunsKit.ScriptedLiveVerifier());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.TestOS, Ct);
        using var person = runtime.As(Who.Person);
        var runs = runtime.Get<IEngineerRuns>(IEngineerRuns.Key(runtime.Brain.Value));

        var none = await runs.InspectEngineerRun(null, Ct);
        Assert.False(none.Ok);
        Assert.Contains("No IntoChat-Engineer (flow.engineer) run was found", none.SummaryText, StringComparison.Ordinal);

        var foreign = new NeuronId(new BrainId("someone-else"), IEngineerRuns.EngineerFlow, "x").ToString();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => runs.SelectEngineerCandidate(foreign, 0, Ct));
    }
}
