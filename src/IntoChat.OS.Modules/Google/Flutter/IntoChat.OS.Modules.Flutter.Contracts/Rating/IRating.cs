using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Rating;

[NeuronAlias("rating"), DefaultNeuronType(UIVocabulary.RatingType)]
public interface IRating
{
    Task Set(int max, int value);
    [ReadOnlyCall] Task<RatingState> Read();
}

[GenerateSerializer, Alias("ui.rating-state")]
[ExportableState]
public sealed class RatingState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public int Max { get; set; } = 5;
    [Id(3)] public int Value { get; set; }
}
