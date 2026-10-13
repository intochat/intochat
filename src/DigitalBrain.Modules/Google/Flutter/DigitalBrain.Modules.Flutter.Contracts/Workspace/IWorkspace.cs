using DigitalBrain;
using Orleans;
using Orleans.Concurrency;

namespace DigitalBrain.Flutter.Workspace;

[Alias("ui.workspace"), Orleans.Metadata.DefaultGrainType("ui.workspace")]
public interface IWorkspace : INeuron
{
    Task<WorkspaceOpenResult> Open(OpenWindow request);
    Task<WorkspaceState> Focus(string? windowId, string operationId, long expectedRevision);
    Task<WorkspaceState> Configure(UiChildRef? home, UiChildRef? companion, long expectedRevision);
    Task<WorkspaceState> SetConnectedSources(IReadOnlyList<string> sources);
    Task<WorkspaceState> Close(string windowId, long expectedRevision, string operationId);
    [ReadOnly] Task<WorkspaceState> Read();
}
