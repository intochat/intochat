using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Progress;

[NeuronAlias("progress"), DefaultNeuronType(UIVocabulary.ProgressType)]
public interface IProgress
{
    Task Set(bool determinate, double value, string label);
    [ReadOnlyCall] Task<ProgressState> Read();
}

[GenerateSerializer, Alias("ui.progress-state")]
[ExportableState]
public sealed class ProgressState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public bool Determinate { get; set; }
    [Id(3)] public double Value { get; set; }
    [Id(4)] public string Label { get; set; } = "";
}
