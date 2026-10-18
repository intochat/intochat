using Orleans.Concurrency;

namespace IntoChat.OS.Flutter.Workspace;

[Alias("ui.workspace"), Orleans.Metadata.DefaultGrainType("ui.workspace")]
public interface IWorkspace : INeuron
{
    Task<WorkspaceState> Apply(WorkspaceUpdate next, string operationId, long expectedRevision);
    Task<WorkspaceState> Show(string app, string operationId, long expectedRevision);
    Task<WorkspaceState> Back(string operationId, long expectedRevision);
    Task<WorkspaceState> Home(string operationId, long expectedRevision);
    Task<WorkspaceState> Close(string app, string operationId, long expectedRevision);
    [ReadOnly] Task<WorkspaceState> Read();
}
