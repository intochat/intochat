using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Workflows;

// A synapse of the Workflow kind: an Agent Framework workflow run hosted by one neuron, keyed by its run id. The neuron's state
// keeps the workflow's checkpoints, so a run that waits for a person survives a restart; a request port's question becomes a
// person-only confirmation and the answer goes back into the workflow.
public enum WorkflowRunStatus { Running, Waiting, Completed, Failed, Cancelled }

public sealed record WorkflowRunView(
    string RunId,
    string Workflow,
    WorkflowRunStatus Status,
    string[] Steps,
    string? PlanId,
    string? Output,
    string? Error);

[Signal("workflow.run-started")]
public sealed record WorkflowRunStarted(string RunId, string Workflow, string Input) : DigitalBrain.Signal;

[Signal("workflow.run-waiting")]
public sealed record WorkflowRunWaiting(string RunId, string PlanId, string Question) : DigitalBrain.Signal;

[Signal("workflow.run-done")]
public sealed record WorkflowRunDone(string RunId, bool Succeeded, string? Output, string? Error, bool Cancelled = false) : DigitalBrain.Signal;

[NeuronAlias("workflow.run"), DefaultNeuronType("workflow.run")]
[Description("One run of a named workflow: start it with text input, read where it is, what it waits for and its output.")]
public interface IWorkflowRun
{
    [Description("Start the workflow named workflow with text input. A run starts once; starting again returns the run as it is.")]
    Task<WorkflowRunView> Start(string workflow, string input);

    [Description("Where this run is: status, the steps done, the approval it waits for, and its output or error.")]
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<WorkflowRunView?> Read();

    [Description("Stop this run: a waiting run withdraws its question and ends Cancelled with the reason. A run that already ended stays as it is.")]
    Task<WorkflowRunView?> Cancel(string reason);
}
