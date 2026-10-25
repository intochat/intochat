using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Clock;

[NeuronAlias("clock"), DefaultNeuronType(UIVocabulary.ClockType)]
public interface IClock
{
    Task Set(string label, DateTimeOffset? dueAt);
    [ReadOnlyCall] Task<ClockState> Read();
}

[GenerateSerializer, Alias("ui.clock-state")]
[ExportableState]
public sealed class ClockState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string Label { get; set; } = "";
    [Id(3)] public DateTimeOffset? DueAt { get; set; }
}
