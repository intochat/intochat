using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Expander;

[NeuronAlias("ui.expander")]
[DefaultNeuronType("ui.expander")]
public interface IExpander
{
    Task<ExpanderState> Configure(string title, string body, bool? isExpanded = null, bool isEnabled = true);
    Task<ExpanderState> Toggle();
    Task<ExpanderState> SetExpanded(bool isExpanded);

    [ReadOnlyCall]
    Task<ExpanderState> Read();
}
