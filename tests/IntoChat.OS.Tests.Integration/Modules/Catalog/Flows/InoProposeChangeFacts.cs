using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Flows;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Tests;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// A change Ino proposes (IStudio.ProposeChange) is an engineering run: flow.engineer with every verification gate on, started
// for the conversation that asked. Ino starts no flow.change.
public sealed class InoProposeChangeFacts : IDisposable
{
    private const string Conversation = "ino/conversations/main";

    private readonly EngineerRunsKit _kit = new("ino-propose");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _kit.Dispose();

    private static IStudio Studio(INeuronRuntime runtime) => runtime.Get<IStudio>(new NeuronId(runtime.Brain, "ino.studio", "studio"));

    private static string FlowIdOf(InoToolResult result)
    {
        var uri = result.Card?.SurfaceScopeUri ?? result.Card?.SourceNeuronUri ?? string.Empty;
        var hash = uri.IndexOf('#', StringComparison.Ordinal);
        Assert.True(hash >= 0, $"The card names no flow: {uri} / {result.Text}");
        return uri[(hash + 1)..];
    }

    [Theory]
    [InlineData(NeuronRuntimeKind.TestOS)]
    [InlineData(NeuronRuntimeKind.Silo)]
    public async Task A_change_Ino_proposes_runs_the_full_loop_and_waits_for_the_person(NeuronRuntimeKind kind)
    {
        var (bareOrigin, _, workTree) = _kit.CreateRepository("ino-" + kind);
        var build = new EngineerRunsKit.CountingBuildVerifier();
        var live = new EngineerRunsKit.ScriptedLiveVerifier();
        await using var fixture = new EngineerRunsKit.Fixture(workTree, build, live);
        await using var runtime = await fixture.Start(kind, Ct);
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        InoToolResult proposed;
        using (runtime.Edge.Enter(runtime.Edge.Agent(new Actor.Person("person", "person"), runtime.Brain, Conversation)))
        {
            proposed = await Studio(runtime).ProposeChange("os", "Add Multiply method to Calculator", Ct);
        }

        Assert.True(proposed.Ok, proposed.Text);
        var flowId = FlowIdOf(proposed);
        Assert.Contains($"/{IEngineerRuns.EngineerFlow}/", flowId, StringComparison.Ordinal);

        using var person = runtime.As(Who.Person);
        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);
        Assert.True(waiting is { Status: FlowStatus.Waiting, WaitingStep: "approval-gate" }, $"{waiting.Status} at {waiting.WaitingStep}: {waiting.Detail}");

        // The one flow: an engineering run.
        Assert.Equal(IEngineerRuns.EngineerFlow, waiting.Type);
        Assert.StartsWith("app:engineer:", waiting.ConfirmationPlanId!, StringComparison.Ordinal);

        // The gates ran: build-and-test and live-verify, once each, and the run records that none was switched off.
        Assert.Equal(1, build.Calls);
        Assert.Equal(1, live.Calls);
        var run = JsonSerializer.Deserialize<EngineerRun>((await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId)).Head()).InnerJson, EngineerFlow.Json)!;
        Assert.Equal(EngineerVerificationPolicy.Full, run.Verification);
        Assert.Equal(1, run.InstanceCount);
        Assert.False(run.RequirePlanRatification);
        Assert.True(run.SelectedInstance!.Build?.Succeeded);
        Assert.True(run.SelectedInstance.Live?.Succeeded);
        Assert.Contains("live-verify", waiting.Done);

        // Ino's context that the call carries: what was asked, where, and for which conversation and person.
        Assert.Equal("Add Multiply method to Calculator", run.Goal);
        Assert.Equal("os", run.Repository?.Value);
        Assert.Equal(Conversation, waiting.Conversation);
        Assert.Equal("person", waiting.Owner);
        Assert.False(string.IsNullOrWhiteSpace(waiting.App));

        // Ino reads the same run back by the id it was given, and by the bare run key.
        InoToolResult status;
        using (runtime.Edge.Enter(runtime.Edge.Agent(new Actor.Person("person", "person"), runtime.Brain, Conversation)))
        {
            status = await Studio(runtime).ChangeStatus(flowId, Ct);
            Assert.Equal("approval", (await Studio(runtime).ChangeStatus(FlowTypeFacts.Id(flowId).Key, Ct)).Card?.Kind);
        }

        Assert.True(status.Ok, status.Text);
        Assert.Equal("approval", status.Card?.Kind);
        Assert.Contains("approval-gate", status.Text, StringComparison.Ordinal);

        // Nothing is pushed before the person approves; then the approved commit is.
        Assert.Equal("", EngineerRunsKit.Git(bareOrigin, "branch", "--list", "ino/*").Trim());
        await runtime.ApproveAsPerson(waiting.ConfirmationPlanId!, Ct);
        var completed = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled);
        Assert.True(completed.Status == FlowStatus.Completed, completed.Detail);
        Assert.Equal(EngineerFlow.Names, completed.Done);
        var published = JsonSerializer.Deserialize<EngineerRun>((await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId)).Head()).InnerJson, EngineerFlow.Json)!;
        Assert.Equal(run.SelectedInstance.Commit!.Commit.Value, EngineerRunsKit.Git(bareOrigin, "rev-parse", published.Publication!.PushedRef!).Trim());
    }

    [Fact]
    public async Task A_change_on_a_branch_carries_the_branch_into_the_run()
    {
        var (_, checkout, workTree) = _kit.CreateRepository("ino-branch");
        EngineerRunsKit.Git(checkout, "branch", "release/1.0");
        EngineerRunsKit.Git(checkout, "push", "origin", "release/1.0");
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), new EngineerRunsKit.ScriptedLiveVerifier());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.TestOS, Ct);
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

        InoToolResult proposed;
        using (runtime.Edge.Enter(runtime.Edge.Agent(new Actor.Person("person", "person"), runtime.Brain, Conversation)))
        {
            proposed = await Studio(runtime).ProposeChangeOnBranch("os", "Add Multiply method to Calculator", "release/1.0", Ct);
        }

        Assert.True(proposed.Ok, proposed.Text);
        var flowId = FlowIdOf(proposed);
        using var person = runtime.As(Who.Person);
        var waiting = await FlowTypeFacts.Eventually(
            () => flows.Read(flowId, Ct),
            v => v.Status is FlowStatus.Waiting or FlowStatus.Failed or FlowStatus.Completed);
        Assert.True(waiting is { Status: FlowStatus.Waiting, WaitingStep: "approval-gate" }, waiting.Detail);

        var run = JsonSerializer.Deserialize<EngineerRun>((await runtime.Get<IFlowStarter>(FlowTypeFacts.Id(flowId)).Head()).InnerJson, EngineerFlow.Json)!;
        Assert.Equal(BranchName.FromRequestedBase("release/1.0"), run.RequestedBaseBranch);
        Assert.Equal("release/1.0", run.SelectedInstance!.Lease!.BaseBranch);

        await flows.Cancel(flowId, "test cleanup", Ct);
    }

    [Fact]
    public async Task A_repository_that_is_not_registered_is_refused_before_any_run_starts()
    {
        var (_, _, workTree) = _kit.CreateRepository("ino-unknown");
        await using var fixture = new EngineerRunsKit.Fixture(workTree, new EngineerRunsKit.CountingBuildVerifier(), new EngineerRunsKit.ScriptedLiveVerifier());
        await using var runtime = await fixture.Start(NeuronRuntimeKind.TestOS, Ct);

        InoToolResult refused;
        using (runtime.Edge.Enter(runtime.Edge.Agent(new Actor.Person("person", "person"), runtime.Brain, Conversation)))
        {
            refused = await Studio(runtime).ProposeChange("no-such-repo", "Add Multiply", Ct);
        }

        Assert.False(refused.Ok);
        Assert.Contains("no-such-repo", refused.Text, StringComparison.Ordinal);
        using var person = runtime.As(Who.Person);
        Assert.Empty(await runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value)).List(new FlowQuery(), Ct));
    }
}
