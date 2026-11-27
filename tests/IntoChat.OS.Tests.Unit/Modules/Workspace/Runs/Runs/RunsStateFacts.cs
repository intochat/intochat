using DigitalBrain.Operations;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Workflows;
using IntoChat.OS.Modules.Apps.AppRuns;
using IntoChat.OS.Modules.Apps.Runs;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// The runs index reads agent turns and operations from their own signals: a turn runs then succeeds or fails, an operation can wait
// for approval in between, and the list shows what is still going first.
public sealed class RunsStateFacts
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void ATurnRunsThenSucceeds()
    {
        var state = new RunsState().Fold(new TurnStarted("owner/ino/conversations/main", "r1", "create a file"), T0)
            .Fold(new TurnCompleted("owner/ino/conversations/main", "r1", "asked for approval"), T0.AddSeconds(5));
        var run = Assert.Single(state.Runs);
        Assert.Equal((RunKind.AgentTurn, RunState.Succeeded, "create a file"), (run.Kind, run.State, run.Title));
        Assert.Equal(T0.AddSeconds(5), run.Ended);
    }

    [Fact]
    public void AnOperationWaitsForApprovalThenFinishes()
    {
        var id = new OperationId("op-1");
        var waiting = new RunsState().Fold(new OperationStarted(id, "WritePlan", "owner/ino.files/ino/files"), T0)
            .Fold(new OperationAwaitingApproval(id, "cf-1"), T0);
        Assert.Equal((RunState.Waiting, "approval cf-1"), (waiting.Runs[0].State, waiting.Runs[0].Waiting));

        var declined = waiting.Fold(new OperationDone(id, new OperationResult.Failed("declined", "The confirmation was denied.")), T0.AddMinutes(1));
        Assert.Equal(RunState.Failed, declined.Runs[0].State);
        Assert.Null(declined.Runs[0].Waiting);
        Assert.Contains("declined", declined.Runs[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAppCommandIsARunOfItsApp()
    {
        var state = new RunsState().Fold(new AppRunStarted("commands/email-notifier/1", "email-notifier", AppTrigger.Command, "test-alert"), T0)
            .Fold(new AppRunDone("commands/email-notifier/1", true, "\"sent\"", null), T0.AddSeconds(1))
            .Fold(new AppRunStarted("ticks/email-notifier/2", "email-notifier", AppTrigger.Schedule, "5m"), T0.AddMinutes(5));
        Assert.Equal(2, state.Runs.Length);
        Assert.Contains(state.Runs, run => (run.Kind, run.State, run.Title, run.Synapse) == (RunKind.App, RunState.Succeeded, "email-notifier: test-alert", "email-notifier"));
        Assert.Contains(state.Runs, run => (run.State, run.Title) == (RunState.Running, "email-notifier: every 5m"));
    }

    [Fact]
    public void AWorkflowDeniedWhileWaitingNoLongerWaits()
    {
        var state = new RunsState().Fold(new WorkflowRunStarted("wf-1", "triage", "count"), T0)
            .Fold(new WorkflowRunWaiting("wf-1", "plan-1", "approve?"), T0)
            .Fold(new WorkflowRunDone("wf-1", false, null, "denied"), T0.AddSeconds(1));
        Assert.Equal(RunState.Failed, state.Runs[0].State);
        Assert.Null(state.Runs[0].Waiting);
    }

    [Fact]
    public void ACancelledRunIsCancelledNotFailed()
    {
        var state = new RunsState().Fold(new WorkflowRunStarted("wf-2", "triage", "count"), T0)
            .Fold(new WorkflowRunWaiting("wf-2", "plan-2", "approve?"), T0)
            .Fold(new WorkflowRunDone("wf-2", false, null, "stop", Cancelled: true), T0.AddSeconds(1));
        Assert.Equal(RunState.Cancelled, state.Runs[0].State);
        Assert.Null(state.Runs[0].Waiting);
    }

    [Fact]
    public void AnEndForAnUnknownRunChangesNothing()
    {
        var state = new RunsState();
        Assert.Same(state, state.Fold(new TurnCompleted("a", "unknown", "x"), T0));
    }

    [Fact]
    public void RunningAndWaitingComeFirst()
    {
        var done = new RunView("done", RunKind.AgentTurn, "", RunState.Succeeded, null, "", null, T0.AddHours(2), T0.AddHours(2), [], null, null);
        var waiting = new RunView("waiting", RunKind.Operation, "", RunState.Waiting, "approval", "", null, T0, null, [], null, null);
        Assert.Equal(["waiting", "done"], RunsState.Order([done, waiting], 10).Select(run => run.Id));
    }
}
