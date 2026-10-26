using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS.Modules.Flutter.Workspace;

namespace IntoChat.OS.Modules.Flutter.Viewport;

[NeuronAlias("viewport"), DefaultNeuronType("viewport")]
public interface IViewport
{
    Task Set(WorkspaceViewRef? target, long expectedRevision);
    [DigitalBrain.ReadOnlyCall] Task<ViewportState> Read();
}

[GenerateSerializer, Alias("ui.viewport-state")]
public sealed record ViewportState([property: Id(0)] string Name, [property: Id(1)] long Revision,
    [property: Id(2)] WorkspaceViewRef? Target);

[Signal("ui.viewport-changed")]
public sealed record ViewportChanged(string Name, long Revision) : DigitalBrain.Signal;
