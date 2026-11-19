using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.Select.Signals;

namespace IntoChat.OS.Modules.UI.Select;

[Neuron("ui.select")]
internal sealed class SelectNeuron : Neuron<SelectState>, ISelect
{
    public async Task<SelectState> Configure(string label, IReadOnlyList<UiSelectOption> options, string? selectedId = null, bool isEnabled = true)
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
            Label = label ?? "",
            Options = [.. options],
            SelectedId = resolved,
            IsEnabled = isEnabled,
        };
        await Save(next, new SelectChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public async Task<SelectState> Select(string optionId)
    {
        if (!State.IsEnabled)
        {
            throw new InvalidOperationException($"Select '{Id.FullKey()}' is disabled.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
        if (State.Options.All(o => !string.Equals(o.Id, optionId, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Option '{optionId}' is not in select '{Id.FullKey()}'.", nameof(optionId));
        }
        if (string.Equals(State.SelectedId, optionId, StringComparison.Ordinal)) { return State; }
        var next = State with { Version = State.Version + 1, SelectedId = optionId };
        await Save(next, new SelectChanged(Id.FullKey(), next.Version, next.SelectedId));
        return next;
    }

    public Task<SelectState> Read() => Task.FromResult(State);
}
