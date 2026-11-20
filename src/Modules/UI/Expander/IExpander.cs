using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Expander;

[NeuronAlias("ui.expander")]
[DefaultNeuronType("ui.expander")]
public interface IExpander : IUiInteractiveControl
{
    Task<UiExpanderState> Configure(string title, string body, bool? isExpanded = null, bool isEnabled = true);
    Task<UiExpanderState> Toggle();
    Task<UiExpanderState> SetExpanded(bool isExpanded);

    [ReadOnlyCall]
    Task<UiExpanderState> Read();
}
