using IntoChat.OS.Modules.Apps.Tasks;

namespace IntoChat.OS.Modules.Apps.Tasks;

/// <summary>
/// Persisted state of <see cref="TasksNeuron"/>. Uses concrete arrays for Orleans serialization compatibility.
/// </summary>
[GenerateSerializer, Alias("tasks.neuron-state")]
internal sealed record TasksNeuronState(
    [property: Id(0)] TaskRecord[] Tasks,
    [property: Id(1)] long Revision,
    [property: Id(2)] int NextSequence)
{
    public TasksNeuronState() : this([], 0, 1) { }
}
