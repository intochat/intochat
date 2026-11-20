using IntoChat.OS.Modules.UI.Core;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.RadioButtons.Signals;

namespace IntoChat.OS.Modules.UI.RadioButtons;

[Neuron("ui.radio-buttons")]
internal sealed class RadioButtonsNeuron : UiNeuronBase<RadioButtonsState>, IRadioButtons
{
    public async Task<RadioButtonsState> Configure(string header, IReadOnlyList<RadioOption> options, string? selectedId = null, bool isEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resolved = selectedId ?? State.SelectedId;
        if (resolved is not null && options.All(o => !string.Equals(o.Id, resolved, StringComparison.Ordinal)))
        {
            resolved = options.FirstOrDefault()?.Id;
        }
        var next = State with
        {
            Version = State.Version + 1,
            Header = header ?? "",
            Options = [.. options],
            SelectedId = resolved,
            IsEnabled = isEnabled,
        };
        await Save(next, new RadioButtonsChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public async Task<RadioButtonsState> Select(string optionId)
    {
        if (!State.IsEnabled) { throw new InvalidOperationException($"RadioButtons '{Id.FullKey()}' is disabled."); }
        if (State.Options.All(o => !string.Equals(o.Id, optionId, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Unknown option '{optionId}'.", nameof(optionId));
        }
        if (string.Equals(State.SelectedId, optionId, StringComparison.Ordinal)) { return State; }
        var next = State with { Version = State.Version + 1, SelectedId = optionId };
        await Save(next, new RadioButtonsChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public Task<RadioButtonsState> Read() => Task.FromResult(State);
}
