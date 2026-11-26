using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Settings;

// Reads a preference a brain stored as a text field before preferences moved into its settings (ILegacyPreferenceField).
// Delete it two releases after the release that carries the adoption (OWED.md).
[Neuron(Type)]
internal sealed class LegacyPreferenceFieldNeuron : Neuron<LegacyPreferenceFieldState>, ILegacyPreferenceField
{
    // The stored type the preferences were kept under; storage keys never move.
    internal const string Type = "ui.field";

    public Task<string?> Value() => Task.FromResult(Id.Key.StartsWith("settings/", StringComparison.Ordinal) ? State.Value : null);
}

// The stored shape of a text-field neuron; only Value is read.
[ExportableState]
internal sealed class LegacyPreferenceFieldState
{
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    public DigitalBrain.UI.FieldKind Kind { get; set; } = DigitalBrain.UI.FieldKind.Text;
    public string? Value { get; set; }
    public long Version { get; set; }
}
