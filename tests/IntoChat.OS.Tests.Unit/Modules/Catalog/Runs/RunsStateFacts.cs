using DigitalBrain.Operations;
using IntoChat.OS.Modules.AI.Agents.Signals;
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
