using IntoChat.OS.Flutter.Workspace;
using Orleans.Concurrency;

namespace IntoChat.OS.Flutter.Viewport;

[Alias("viewport"), Orleans.Metadata.DefaultGrainType("viewport")]
public interface IViewport : INeuron
{
    Task Set(WorkspaceViewRef? target, long expectedRevision);
    [ReadOnly] Task<ViewportState> Read();
}

[GenerateSerializer, Alias("ui.viewport-state")]
public sealed record ViewportState([property: Id(0)] string Name, [property: Id(1)] long Revision,
    [property: Id(2)] WorkspaceViewRef? Target);

[GenerateSerializer, Alias("ui.viewport-changed")]
public sealed record ViewportChanged([property: Id(0)] string Name, [property: Id(1)] long Revision) : Signal;
