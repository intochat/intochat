using System;
using System.Threading.Tasks;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.AI.Agents.Runs;

[Orleans.GenerateSerializer, Orleans.Alias("agent.run.state")]
public sealed record RunState(
    [property: Orleans.Id(0)] string Target,
    [property: Orleans.Id(1)] string? TaskId,
    [property: Orleans.Id(2)] string Status,
    [property: Orleans.Id(3)] DateTimeOffset Started,
    [property: Orleans.Id(4)] DateTimeOffset LastTurn)
{
    public RunState() : this("", null, "Idle", default, default) { }
}

[Signal("agent.run.changed")]
public sealed record RunChanged(
    string RunKey,
    string Target,
    string? TaskId,
    string Status,
    DateTimeOffset LastTurn) : Signal;

[NeuronAlias("agent.run"), DefaultNeuronType("agent.run")]
[Shareable]
public interface IRunNeuron
{
    Task<RunState> StartAsync(string target, string? taskId = null);

    Task<RunState> TouchAsync(string? status = null);

    Task<RunState> CompleteAsync(string status = "Completed");

    [ReadOnlyCall]
    Task<RunState> ReadAsync();
}
