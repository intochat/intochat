using System.ComponentModel;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Runs;

// One list of everything that runs in a brain, whatever expressed it: a flow, an agent turn, or an operation (a storage write
// waiting for approval, for example). Each kind keeps its own engine and signals; this is the one place that reads them together.
public enum RunKind { Flow, AgentTurn, Operation }

public enum RunState { Running, Waiting, Succeeded, Failed, Cancelled }

public sealed record RunView(
    string Id,
    RunKind Kind,
    string Title,
    RunState State,
    string? Waiting,
    string Owner,
    string? TraceId,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    string[] Steps,
    string? Current,
    string? Detail);

[DigitalBrain.Signal("runs.changed")]
public sealed record RunsChanged(int Count) : DigitalBrain.Signal;

[NeuronAlias("runs"), DefaultNeuronType("runs")]
[Description("Everything running in this brain, by id: flows, agent turns and operations, with state, steps and what each waits for.")]
public interface IRuns
{
    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "runs", "runs/index");

    [Description("The newest runs, running and waiting ones first. limit is at most 200.")]
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<RunView[]> List(int limit = 50);

    [Description("One run by its id (a flow id, an agent run id or an operation id).")]
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<RunView?> Read(string id);
}
