using IntoChat.OS;

namespace IntoChat.OS.Modules.Flutter.Workspace.Signals;

[GenerateSerializer, Alias("ui.workspace-changed")]
public sealed record WorkspaceChanged(
    [property: Id(0)] string WorkspaceId,
    [property: Id(1)] long Revision) : Signal;
