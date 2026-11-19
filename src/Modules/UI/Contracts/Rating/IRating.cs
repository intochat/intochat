using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Rating;

[NeuronAlias("ui.rating")]
[DefaultNeuronType("ui.rating")]
public interface IRating
{
    Task<RatingState> Configure(string label, int value = 0, int max = 5, bool isEnabled = true);
    Task<RatingState> SetValue(int value);

    [ReadOnlyCall]
    Task<RatingState> Read();
}
