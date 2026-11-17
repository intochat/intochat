using DigitalBrain.Operations;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Workflows;
using IntoChat.OS.Modules.Apps.CodeBlocks;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Runs;

// The brain's runs index: agent turns and operations folded from their signals on one brain-wide wire, flows read from the flows
// index. Like the flows index it hears what happens after it first wakes.
[DigitalBrain.Neurons.Neuron("runs")]
internal sealed class RunsNeuron : DigitalBrain.Neurons.Neuron<RunsState>, IRuns
{
    protected override async Task OnActivate(CancellationToken cancellationToken)
        => await Context.Wires.EnsureBrainWide(typeof(TurnStarted), typeof(TurnCompleted), typeof(TurnFailed),
            typeof(OperationStarted), typeof(OperationAwaitingApproval), typeof(OperationDone),
            typeof(WorkflowRunStarted), typeof(WorkflowRunWaiting), typeof(WorkflowRunDone), typeof(CodeBlockRunStarted), typeof(CodeBlockRunDone));

    public async Task<RunView[]> List(int limit = 50)
        => RunsState.Order([.. await Flows(), .. State.Runs], limit);

    public async Task<RunView?> Read(string id)
        => State.Runs.FirstOrDefault(run => run.Id == id) ?? (await Flows()).FirstOrDefault(run => run.Id == id);

    [DigitalBrain.Neurons.On] public Task On(TurnStarted signal) => Fold(signal);
    [DigitalBrain.Neurons.On] public Task On(TurnCompleted signal) => Fold(signal);
    [DigitalBrain.Neurons.On] public Task On(TurnFailed signal) => Fold(signal);
    [DigitalBrain.Neurons.On] public Task On(OperationStarted signal) => Fold(signal);
    [DigitalBrain.Neurons.On] public Task On(OperationAwaitingApproval signal) => Fold(signal);
    [DigitalBrain.Neurons.On] public Task On(OperationDone signal) => Fold(signal);
    [DigitalBrain.Neurons.On] public Task On(WorkflowRunStarted signal) => Fold(signal);
    [DigitalBrain.Neurons.On] public Task On(WorkflowRunWaiting signal) => Fold(signal);
    [DigitalBrain.Neurons.On] public Task On(WorkflowRunDone signal) => Fold(signal);
    [DigitalBrain.Neurons.On] public Task On(CodeBlockRunStarted signal) => Fold(signal);
    [DigitalBrain.Neurons.On] public Task On(CodeBlockRunDone signal) => Fold(signal);

    private async Task Fold(DigitalBrain.Signal signal)
    {
        var next = State.Fold(signal, Context.Time.GetUtcNow());
        if (!ReferenceEquals(next, State)) { await Save(next, new RunsChanged(next.Runs.Length)); }
    }

    private async Task<RunView[]> Flows()
        => [.. (await Context.Get<IFlows>(IFlows.Key(Id.Brain.Value)).List(new FlowQuery(Limit: 200))).Select(RunsState.Of)];
}
