using DigitalBrain;
using DigitalBrain.Contracts;
using IntoChat.OS.Modules.Flutter.Workspace;

namespace IntoChat.OS.Modules.Flutter.Viewport;

[NeuronAlias("viewport"), DefaultNeuronType("viewport")]
public interface IViewport
{
    Task Set(WorkspaceViewRef? target, long expectedRevision);
    [DigitalBrain.Contracts.ReadOnlyCall] Task<ViewportState> Read();
}

public sealed record ViewportState(string Name, long Revision,
    WorkspaceViewRef? Target);

[Signal("ui.viewport-changed")]
public sealed record ViewportChanged(string Name, long Revision) : DigitalBrain.Signal;
