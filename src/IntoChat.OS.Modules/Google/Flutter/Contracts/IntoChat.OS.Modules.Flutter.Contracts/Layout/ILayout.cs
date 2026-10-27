using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Layout;

[NeuronAlias("layout"), DefaultNeuronType(UIVocabulary.LayoutType)]
public interface ILayout
{
    Task Set(LayoutDefinition definition, long expectedRevision);
    [DigitalBrain.ReadOnlyCall] Task<LayoutState> Read();
}

[GenerateSerializer, Alias("ui.layout-definition")]
public sealed record LayoutDefinition([property: Id(0)] string Mode, [property: Id(1)] UiChildRef[] Children, [property: Id(2)] double Gap = 12, [property: Id(3)] double[]? Extents = null,
    [property: Id(4)] bool FollowEnd = false,
    [property: Id(5)] string? Surface = null, [property: Id(6)] double Padding = 0);

[GenerateSerializer, Alias("ui.layout-state")]
[ExportableState]
public sealed class LayoutState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public long Revision { get; set; }
    [Id(2)] public LayoutDefinition Definition { get; set; } = new("column", []);
}

[Signal("ui.layout-changed")]
public sealed record LayoutChanged(string Name, long Revision) : DigitalBrain.Signal;
