using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Entries;

[GenerateSerializer, Alias("ui.entry-control-mode")]
public enum EntryControlMode { Key = 0, Collection = 1 }

// Which entries the control edits. It names an entry, never a value: values travel only from the renderer's secret
// field to PUT /brains/{brainId}/entries/{name} over the person's session, which is owner-only.
[GenerateSerializer, Alias("ui.entry-control-spec")]
public sealed record EntryControlSpec(
    [property: Id(0)] EntryControlMode Mode,
    [property: Id(1)] string Label,
    [property: Id(2)] string? EntryName = null);

[NeuronAlias("entrycontrol"), DefaultNeuronType(UIVocabulary.EntryControlType)]
public interface IEntryControl
{
    Task Configure(EntryControlSpec spec);
    [DigitalBrain.ReadOnlyCall] Task<EntryControlState> Read();
    // The renderer calls this after a successful write so the app can refresh; the signal carries only the neuron name.
    Task Changed();
}

[GenerateSerializer, Alias("ui.entry-control-state")]
[ExportableState]
public sealed class EntryControlState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public EntryControlSpec? Spec { get; set; }
}
