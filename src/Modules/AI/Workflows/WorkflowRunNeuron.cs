using DigitalBrain.Confirmation;
using Microsoft.Agents.AI.Workflows;

namespace IntoChat.OS.Modules.AI.Workflows;

internal sealed record WorkflowRunState(
    string RunId,
    string Workflow,
    WorkflowRunStatus Status,
    string[] Steps,
    string? PlanId,
    string? Output,
    string? Error,
    string? CheckpointId,
    string[] CheckpointKeys,
    string[] CheckpointTexts)
{
    public WorkflowRunState() : this("", "", WorkflowRunStatus.Running, [], null, null, null, null, [], []) { }

    internal bool Exists => RunId.Length > 0;

    internal WorkflowRunView View() => new(RunId, Workflow, Status, Steps, PlanId, Output, Error);

    internal Dictionary<string, string> Checkpoints()
        => CheckpointKeys.Zip(CheckpointTexts).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);

    internal WorkflowRunState With(IDictionary<string, string> checkpoints)
        => this with { CheckpointKeys = [.. checkpoints.Keys], CheckpointTexts = [.. checkpoints.Values] };
}

// Hosts one workflow run (key = run id). Each turn runs the workflow until it finishes or halts on a request, saving its checkpoints in
// this neuron's state. A request becomes a person-only confirmation; when it is answered (or found answered after a restart) the run
// resumes from its last checkpoint, which announces the request again, and the answer is sent back.
[DigitalBrain.Neurons.Neuron("workflow.run")]
internal sealed class WorkflowRunNeuron(IEnumerable<WorkflowDefinition> definitions, DigitalBrain.Access.EdgeAuthority? edge = null)
    : DigitalBrain.Neurons.Neuron<WorkflowRunState>, IWorkflowRun
{
    public async Task<WorkflowRunView> Start(string workflow, string input)
    {
        if (State.Exists) { return State.View(); }
        var definition = Definition(workflow);
        await Save(new WorkflowRunState() with { RunId = Id.Key, Workflow = definition.Name },
            new WorkflowRunStarted(Id.Key, definition.Name, input ?? ""));
        var checkpoints = State.Checkpoints();
        await using var run = await InProcessExecution.RunStreamingAsync(definition.Build(), input ?? "",
            CheckpointManager.CreateJson(new StateCheckpointStore(checkpoints)), Id.Key, CancellationToken.None);
        await Advance(run, checkpoints, answer: null);
        return State.View();
    }

    public Task<WorkflowRunView?> Read() => Task.FromResult(State.Exists ? State.View() : null);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        // A restart while waiting: the answer may already be in; otherwise keep listening for it.
        if (State.Status == WorkflowRunStatus.Waiting && State.PlanId is { } planId)
        {
            ConfirmationResult? outcome;
            using (AsWorkflows()) { outcome = await Confirmations().Outcome(planId); }
            if (outcome is not null) { await Resume(outcome); }
            else { using (AsWorkflows()) { await Context.Wires.Ensure<ConfirmationResolved>(IConfirmationRequests.Of(Id.Brain)); } }
        }
    }

    [DigitalBrain.Neurons.On]
    public async Task OnResolved(ConfirmationResolved resolved)
    {
        if (State.Status != WorkflowRunStatus.Waiting || !string.Equals(resolved.PlanId, State.PlanId, StringComparison.Ordinal)) { return; }
        ConfirmationResult? outcome;
        using (AsWorkflows()) { outcome = await Confirmations().Outcome(resolved.PlanId); }
        if (outcome is not null) { await Resume(outcome); }
    }

    private async Task Resume(ConfirmationResult outcome)
    {
        var approved = outcome.Outcome == ConfirmationOutcome.Approved;
        var checkpoints = State.Checkpoints();
        await using var run = await InProcessExecution.ResumeStreamingAsync(Definition(State.Workflow).Build(),
            new CheckpointInfo(Id.Key, State.CheckpointId!), CheckpointManager.CreateJson(new StateCheckpointStore(checkpoints)), CancellationToken.None);
        await Advance(run, checkpoints, approved);
    }

    // Watches the run until it halts. With an answer, the re-announced request is answered first and the run is watched again.
    private async Task Advance(StreamingRun run, Dictionary<string, string> checkpoints, bool? answer)
    {
        var steps = State.Steps.ToList();
        string? checkpoint = State.CheckpointId, output = null, error = null;
        ExternalRequest? request = null;
        for (var pass = 0; pass < 2; pass++)
        {
            request = null;
            await foreach (var evt in run.WatchStreamAsync(blockOnPendingRequest: false, CancellationToken.None))
            {
                switch (evt)
                {
                    case ExecutorCompletedEvent done when done.ExecutorId is { } executor && (steps.Count == 0 || steps[^1] != executor): steps.Add(executor); break;
                    case SuperStepCompletedEvent step when step.CompletionInfo?.Checkpoint is { } info: checkpoint = info.CheckpointId; break;
                    case RequestInfoEvent asked: request = asked.Request; break;
                    case WorkflowOutputEvent result: output = result.Data?.ToString(); break;
                    case WorkflowErrorEvent failed: error = failed.Data?.ToString() ?? "The workflow failed."; break;
                }
            }
            if (answer is { } value && request is not null && pass == 0)
            {
                await run.SendResponseAsync(request.CreateResponse(value));
                answer = null;
                continue;
            }
            break;
        }

        var next = State.With(checkpoints) with { Steps = [.. steps], CheckpointId = checkpoint };
        if (error is not null || output is not null)
        {
            await Save(next with { Status = error is null ? WorkflowRunStatus.Completed : WorkflowRunStatus.Failed, Output = output, Error = error, PlanId = null },
                new WorkflowRunDone(Id.Key, error is null, output, error));
            return;
        }

        if (request is null)
        {
            await Save(next with { Status = WorkflowRunStatus.Failed, Error = "The workflow stopped without an output or a question." },
                new WorkflowRunDone(Id.Key, false, null, "The workflow stopped without an output or a question."));
            return;
        }

        var question = request.Data.As<string>() ?? request.Data.ToString() ?? "";
        var planId = $"wf-{Id.Key}-{request.RequestId}";
        using (AsWorkflows())
        {
            await Context.Wires.Ensure<ConfirmationResolved>(IConfirmationRequests.Of(Id.Brain));
        }
        await Save(next with { Status = WorkflowRunStatus.Waiting, PlanId = planId }, new WorkflowRunWaiting(Id.Key, planId, question));
        try
        {
            using (AsWorkflows())
            {
                await Confirmations().Request(new ConfirmationPlan(planId, ConfirmationAction.Other, false, $"Continue {State.Workflow}?",
                    $"The workflow {State.Workflow} asks: {question}", "Continue", "Stop", State.Workflow) { RequiresPerson = true, Kind = "workflow.request", Subject = Id.Key });
            }
        }
        catch (Exception refused) when (refused is UnauthorizedAccessException or InvalidOperationException)
        {
            // Nobody could ever answer a request that was never filed, so the run ends instead of waiting forever.
            var reason = $"The workflow could not ask for approval: {refused.Message}";
            await Save(State with { Status = WorkflowRunStatus.Failed, Error = reason, PlanId = null }, new WorkflowRunDone(Id.Key, false, null, reason));
        }
    }

    private WorkflowDefinition Definition(string name)
        => definitions.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException($"No workflow named '{name}'. Known: {string.Join(", ", definitions.Select(item => item.Name))}.");

    private IConfirmationRequests Confirmations() => Context.Get<IConfirmationRequests>(IConfirmationRequests.Of(Id.Brain));

    // Asking for and reading approvals is the run's own business, whoever started it: an app that may start a workflow is not thereby
    // allowed to reach the brain's confirmations. Answering one stays the person's act.
    private IDisposable? AsWorkflows() => edge?.Enter(edge.Module("ai.workflows", Id.Brain));
}
