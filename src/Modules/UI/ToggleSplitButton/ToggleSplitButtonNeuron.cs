using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.ToggleSplitButton.Signals;

namespace IntoChat.OS.Modules.UI.ToggleSplitButton;

[Neuron("ui.toggle-split-button")]
internal sealed class ToggleSplitButtonNeuron : Neuron<ToggleSplitButtonState>, IToggleSplitButton
{
    public async Task<ToggleSplitButtonState> Configure(
        string label,
        IReadOnlyList<ToggleSplitButtonOption> options,
        bool isChecked = false,
        string? icon = null,
        string? selectedOptionId = null,
        bool isEnabled = true)
    {
        var arr = options is not null ? [.. options] : Array.Empty<ToggleSplitButtonOption>();
        var next = State with
        {
            Version = State.Version + 1,
            Label = string.IsNullOrWhiteSpace(label) ? "Toggle" : label.Trim(),
            Icon = string.IsNullOrWhiteSpace(icon) ? null : icon.Trim(),
            IsChecked = isChecked,
            Options = arr,
            SelectedOptionId = selectedOptionId ?? arr.FirstOrDefault()?.Id,
            IsEnabled = isEnabled,
        };
        await Save(next, new ToggleSplitButtonChanged(Id.FullKey(), next.Version, next.IsChecked, next.SelectedOptionId));
        return next;
    }

    public Task<ToggleSplitButtonState> Toggle() => SetChecked(!State.IsChecked);

    public async Task<ToggleSplitButtonState> SetChecked(bool isChecked)
    {
        if (!State.IsEnabled)
        {
            throw new InvalidOperationException("ToggleSplitButton is disabled.");
        }
        var next = State with
        {
            Version = State.Version + 1,
            IsChecked = isChecked,
        };
        await Save(next, new ToggleSplitButtonChanged(Id.FullKey(), next.Version, next.IsChecked, next.SelectedOptionId));
        return next;
    }

    public async Task<ToggleSplitButtonState> SelectOption(string optionId)
    {
        if (!State.IsEnabled)
        {
            throw new InvalidOperationException("ToggleSplitButton is disabled.");
        }
        var opt = State.Options.FirstOrDefault(o => string.Equals(o.Id, optionId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Unknown toggle split button option '{optionId}'.", nameof(optionId));

        var next = State with
        {
            Version = State.Version + 1,
            Label = opt.Label,
            Icon = opt.Icon ?? State.Icon,
            SelectedOptionId = opt.Id,
            IsChecked = true,
        };
        await Save(next, new ToggleSplitButtonChanged(Id.FullKey(), next.Version, next.IsChecked, next.SelectedOptionId));
        return next;
    }

    public Task<ToggleSplitButtonState> Read() => Task.FromResult(State);
}
