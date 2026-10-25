using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Surface;

[NeuronAlias("surface"), DefaultNeuronType(UIVocabulary.SurfaceType)]
public interface ISurface
{
    Task Set(SurfaceDefinition definition, long expectedRevision);
    [ReadOnlyCall] Task<SurfaceState> Read();
}

[GenerateSerializer, Alias("ui.surface-definition")]
public sealed record SurfaceDefinition([property: Id(0)] string Title, [property: Id(1)] UiChildRef[] Children);

[GenerateSerializer, Alias("ui.surface-state")]
[ExportableState]
public sealed class SurfaceState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public long Revision { get; set; }
    [Id(2)] public SurfaceDefinition Definition { get; set; } = new("", []);
}

[Signal("ui.surface-changed")]
public sealed record SurfaceChanged(string Name, long Revision) : DigitalBrain.Signal;
