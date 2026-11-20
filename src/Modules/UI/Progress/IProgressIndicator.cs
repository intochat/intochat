using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.Progress;

[NeuronAlias("ui.progress")]
[DefaultNeuronType("ui.progress")]
public interface IProgressIndicator : IUiInteractiveControl
{
    Task<ProgressState> SetProgress(string label, double value, bool isIndeterminate = false, string kind = "bar");

    [ReadOnlyCall]
    Task<ProgressState> Read();
}
