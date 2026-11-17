using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Contracts;
using Microsoft.Agents.AI.Workflows;

namespace IntoChat.OS.Modules.AI.Workflows;

// A synapse of the Workflow kind: an Agent Framework workflow run hosted by one neuron, keyed by its run id. The neuron's state
// keeps the workflow's checkpoints, so a run that waits for a person survives a restart; a request port's question becomes a
// person-only confirmation and the answer goes back into the workflow.
public enum WorkflowRunStatus { Running, Waiting, Completed, Failed }

public sealed record WorkflowRunView(
    string RunId,
    string Workflow,
    WorkflowRunStatus Status,
    string[] Steps,
    string? PlanId,
    string? Output,
    string? Error);

// A workflow a host offers by name; Build makes a fresh instance for each run.
public sealed record WorkflowDefinition(string Name, string Description, Func<Workflow> Build);

[Signal("workflow.run-started")]
public sealed record WorkflowRunStarted(string RunId, string Workflow, string Input) : DigitalBrain.Signal;

[Signal("workflow.run-waiting")]
public sealed record WorkflowRunWaiting(string RunId, string PlanId, string Question) : DigitalBrain.Signal;

[Signal("workflow.run-done")]
public sealed record WorkflowRunDone(string RunId, bool Succeeded, string? Output, string? Error) : DigitalBrain.Signal;

[NeuronAlias("workflow.run"), DefaultNeuronType("workflow.run")]
[Description("One run of a named workflow: start it with text input, read where it is, what it waits for and its output.")]
public interface IWorkflowRun
{
    [Description("Start the workflow named workflow with text input. A run starts once; starting again returns the run as it is.")]
    Task<WorkflowRunView> Start(string workflow, string input);

    [Description("Where this run is: status, the steps done, the approval it waits for, and its output or error.")]
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<WorkflowRunView?> Read();
}
