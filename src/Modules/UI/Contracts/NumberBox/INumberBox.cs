using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.NumberBox;

[NeuronAlias("ui.number-box")]
[DefaultNeuronType("ui.number-box")]
public interface INumberBox
{
    Task<NumberBoxState> Configure(string label, double value = 0, double min = 0, double max = 1000, double step = 1, bool isEnabled = true, string prefix = "");
    Task<NumberBoxState> SetValue(double value);
    Task<NumberBoxState> Increment();
    Task<NumberBoxState> Decrement();

    [ReadOnlyCall]
    Task<NumberBoxState> Read();
}
