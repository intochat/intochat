using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Preferences;

// Where a preference lives. The first block is the brain's {brain}/settings text fields, which any member may
// change; the Local* block is this device's own choice, which the renderer reads and writes on the device.
[GenerateSerializer, Alias("ui.preference-store")]
public enum PreferenceStore
{
    NotifyAgentFinished = 0,
    NotifyNeedsApproval = 1,
    NotifyConnectionErrors = 2,
    NotifyProductUpdates = 3,
    NewTabApp = 4,
    LocalDisplayName = 10,
    LocalTheme = 11,
    LocalCompactSpacing = 12,
    LocalReducedMotion = 13,
    LocalDefaultModel = 14,
}

[GenerateSerializer, Alias("ui.preference-kind")]
public enum PreferenceKind { Toggle = 0, Choice = 1, Text = 2 }

[GenerateSerializer, Alias("ui.preference-choice")]
public sealed record PreferenceChoice(
    [property: Id(0)] string Id,
    [property: Id(1)] string Label,
    [property: Id(2)] bool Enabled = true,
    [property: Id(3)] string? Detail = null);

[GenerateSerializer, Alias("ui.preference-spec")]
public sealed record PreferenceSpec(
    [property: Id(0)] PreferenceStore Store,
    [property: Id(1)] string Label,
    [property: Id(2)] PreferenceKind Kind,
    [property: Id(3)] PreferenceChoice[]? Choices = null);

[NeuronAlias("preference"), DefaultNeuronType(UIVocabulary.PreferenceType)]
public interface IPreferenceControl
{
    Task Configure(PreferenceSpec spec);
    // The spec plus the stored value; Value is null for a Local* store, which only the device holds.
    [ReadOnlyCall] Task<PreferenceControlState> Read();
    // Writes a brain store as the calling person. Installed apps are refused; Local* stores are never written here.
    Task Set(string value);
}

[GenerateSerializer, Alias("ui.preference-control-state")]
[ExportableState]
public sealed class PreferenceControlState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public PreferenceSpec? Spec { get; set; }
    [Id(3)] public string? Value { get; set; }
}
