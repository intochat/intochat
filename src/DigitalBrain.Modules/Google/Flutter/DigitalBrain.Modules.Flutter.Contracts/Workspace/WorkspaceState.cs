namespace DigitalBrain.Flutter.Workspace;

[GenerateSerializer, Alias("ui.workspace-state")]
public sealed record WorkspaceState(
    [property: Id(0)] long Revision,
    [property: Id(1)] WorkspaceWindow[] Windows,
    [property: Id(2)] FirstRunState? FirstRun = null,
    [property: Id(3)] string? FocusedWindow = null,
    [property: Id(4)] UiChildRef? Home = null,
    [property: Id(5)] UiChildRef? Companion = null);
