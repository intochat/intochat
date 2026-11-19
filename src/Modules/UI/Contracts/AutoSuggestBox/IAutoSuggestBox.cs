using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.AutoSuggestBox;

[NeuronAlias("ui.auto-suggest")]
[DefaultNeuronType("ui.auto-suggest")]
public interface IAutoSuggestBox
{
    Task<AutoSuggestBoxState> Configure(string placeholder, IReadOnlyList<string> items);
    Task<AutoSuggestBoxState> SetQuery(string query);
    Task<AutoSuggestBoxState> Submit(string text);

    [ReadOnlyCall]
    Task<AutoSuggestBoxState> Read();
}
