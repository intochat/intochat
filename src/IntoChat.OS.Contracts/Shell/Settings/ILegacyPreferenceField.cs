using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Flutter.Settings;

// Reads a preference a brain stored before preferences moved into the brain's settings (IUserSettings): one text-field neuron
// ("ui.field") per preference under {brain}/settings. BrainPreferenceStore adopts what it holds once and never writes here.
// Delete it two releases after the release that carries the adoption (OWED.md).
[NeuronAlias("shell.legacy-preference-field"), DefaultNeuronType("ui.field")]
public interface ILegacyPreferenceField
{
    // The stored text, or null when none was stored. Only keys under settings/ answer: a field an app view once kept answers null.
    [ReadOnlyCall] Task<string?> Value();
}

