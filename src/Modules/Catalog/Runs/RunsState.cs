using DigitalBrain.Flows;
using DigitalBrain.Operations;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Workflows;
using IntoChat.OS.Modules.Apps.AppRuns;
using IntoChat.OS.Modules.Apps.CodeBlocks;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Runs;

// The agent turns, workflow runs and operations folded from their own signals; flows are not copied here, the flows index keeps them.
internal sealed record RunsState(RunView[] Runs)
{
    internal const int Kept = 500;

    public RunsState() : this([]) { }

    internal RunsState Fold(DigitalBrain.Signal signal, DateTimeOffset at) => signal switch
    {
        TurnStarted started => Put(new RunView(started.RunId, RunKind.AgentTurn, Title(started.UserText), RunState.Running, null,
            started.AgentId, null, at, null, [], "answering", null, AgentApp(started.AgentId))),
        TurnCompleted completed => Update(completed.RunId, run => run with { State = RunState.Succeeded, Ended = at, Current = null, Detail = Title(completed.Reply) }),
        TurnFailed failed => Update(failed.RunId, run => run with
        {
            State = failed.Cancelled ? RunState.Cancelled : RunState.Failed,
            Ended = at,
            Current = null,
            Detail = failed.Error,
        }),
        OperationStarted started => Put(new RunView(started.Operation.Value, RunKind.Operation, started.Kind, RunState.Running, null,
            started.Origin, null, at, null, [], null, null, started.Kind)),
        OperationAwaitingApproval awaiting => Update(awaiting.Operation.Value, run => run with { State = RunState.Waiting, Waiting = $"approval {awaiting.PlanId}" }),
        OperationDone done => Update(done.Operation.Value, run => run with { State = Outcome(done.Result), Waiting = null, Ended = at, Detail = Describe(done.Result) }),
        WorkflowRunStarted started => Put(new RunView(started.RunId, RunKind.Workflow, $"{started.Workflow}: {Title(started.Input)}", RunState.Running, null,
            started.Workflow, null, at, null, [], null, null, started.Workflow)),
        WorkflowRunWaiting waiting => Update(waiting.RunId, run => run with { State = RunState.Waiting, Waiting = $"approval {waiting.PlanId}", Detail = Title(waiting.Question) }),
        WorkflowRunDone done => Update(done.RunId, run => run with
        {
            State = done.Cancelled ? RunState.Cancelled : done.Succeeded ? RunState.Succeeded : RunState.Failed,
            Waiting = null,
            Ended = at,
            Detail = done.Error ?? Title(done.Output),
        }),
        CodeBlockRunStarted started => Put(new RunView(started.RunId, RunKind.CodeBlock, started.Title, RunState.Running, null,
            "sandbox", null, at, null, [], "compile, deploy, run", null, "code block")),
        CodeBlockRunWaiting waiting => Update(waiting.RunId, run => run with { State = RunState.Waiting, Waiting = $"approval {waiting.PlanId}", Detail = "needs: " + string.Join(", ", waiting.Needs) }),
        CodeBlockRunDone done => Update(done.RunId, run => run with
        {
            State = done.Cancelled ? RunState.Cancelled : done.Succeeded ? RunState.Succeeded : RunState.Failed,
            Waiting = null,
            Ended = at,
            Current = null,
            Detail = done.Error ?? Title(done.Output),
        }),
        AppRunStarted started => Put(new RunView(started.RunId, RunKind.App, started.Trigger == AppTrigger.Schedule ? $"{started.App}: every {started.Name}" : $"{started.App}: {started.Name}",
            RunState.Running, null, started.App, null, at, null, [], started.Name, null, started.App)),
        AppRunDone done => Update(done.RunId, run => run with
        {
            State = done.Succeeded ? RunState.Succeeded : RunState.Failed,
            Ended = at,
            Current = null,
            Detail = done.Error ?? Title(done.Output),
        }),
        _ => this,
    };

    // A flow as a run, from the flows index's own view.
    internal static RunView Of(FlowView flow) => new(flow.FlowId, RunKind.Flow, flow.Title, flow.Status switch
    {
        FlowStatus.Waiting or FlowStatus.Blocked => RunState.Waiting,
        FlowStatus.Completed => RunState.Succeeded,
        FlowStatus.Failed => RunState.Failed,
        FlowStatus.Cancelled => RunState.Cancelled,
        _ => RunState.Running,
    }, flow.WaitingFor is { } reason ? $"{reason.ToString().ToLowerInvariant()} {flow.ConfirmationPlanId ?? flow.WaitingStep}".Trim() : null,
        flow.Owner, flow.TraceId, flow.Started, flow.Ended, flow.Steps, flow.Current, flow.Detail, flow.Type);

    // "{brain}/{app}/conversations/{id}" belongs to the app's agent.
    internal static string AgentApp(string agentId)
    {
        var parts = agentId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : agentId;
    }

    // Running and waiting first, then newest first.
    internal static RunView[] Order(IEnumerable<RunView> runs, int limit)
        => [.. runs.OrderBy(run => run.State is RunState.Running or RunState.Waiting ? 0 : 1).ThenByDescending(run => run.Started).Take(Math.Clamp(limit, 1, 200))];

    private RunsState Put(RunView run)
        => this with { Runs = [.. Runs.Where(item => item.Id != run.Id).TakeLast(Kept - 1), run] };

    private RunsState Update(string id, Func<RunView, RunView> change)
        => Array.FindIndex(Runs, run => run.Id == id) is var index and >= 0 ? this with { Runs = Runs.Select((run, i) => i == index ? change(run) : run).ToArray() } : this;

    private static RunState Outcome(OperationResult result) => result switch
    {
        OperationResult.Succeeded or OperationResult.Planned => RunState.Succeeded,
        OperationResult.Cancelled => RunState.Cancelled,
        _ => RunState.Failed,
    };

    private static string? Describe(OperationResult result) => result is OperationResult.Failed failed ? $"{failed.Code}: {failed.Message}" : null;

    private static string Title(string? text) => string.IsNullOrWhiteSpace(text) ? "" : text.Length <= 120 ? text : text[..120] + "…";
}
