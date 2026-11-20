using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.InfoBar;

[NeuronAlias("ui.info-bar")]
[DefaultNeuronType("ui.info-bar")]
public interface IInfoBar : IUiInteractiveControl
{
    Task<InfoBarState> Show(string title, string message, string severity = "info", bool isClosable = true);
    Task<InfoBarState> Dismiss();

    [ReadOnlyCall]
    Task<InfoBarState> Read();
}
