using DigitalBrain.Neurons;
using IntoChat.OS.Modules.UI.ColorPicker.Signals;

namespace IntoChat.OS.Modules.UI.ColorPicker;

[Neuron("ui.color-picker")]
internal sealed class ColorPickerNeuron : Neuron<ColorPickerState>, IColorPicker
{
    public async Task<ColorPickerState> Configure(string label, string hex = "#1F5EFF", IReadOnlyList<string>? swatches = null, bool isEnabled = true)
    {
        var next = State with
        {
            Version = State.Version + 1,
            Label = label ?? "",
            Hex = NormalizeHex(hex),
            Swatches = swatches is { Count: > 0 } ? [.. swatches] : [.. ColorPickerState.DefaultSwatches],
            IsEnabled = isEnabled,
        };
        await Save(next, new ColorChanged(Id.FullKey(), next.Version, next.Hex));
        return next;
    }

    public async Task<ColorPickerState> SetColor(string hex)
    {
        if (!State.IsEnabled) { throw new InvalidOperationException($"ColorPicker '{Id.FullKey()}' is disabled."); }
        var normalized = NormalizeHex(hex);
        var next = State with { Version = State.Version + 1, Hex = normalized };
        await Save(next, new ColorChanged(Id.FullKey(), next.Version, next.Hex));
        return next;
    }

    public Task<ColorPickerState> Read() => Task.FromResult(State);

    private static string NormalizeHex(string? hex)
    {
        var trimmed = (hex ?? "#1F5EFF").Trim();
        return trimmed.StartsWith('#') ? trimmed.ToUpperInvariant() : "#" + trimmed.ToUpperInvariant();
    }
}
