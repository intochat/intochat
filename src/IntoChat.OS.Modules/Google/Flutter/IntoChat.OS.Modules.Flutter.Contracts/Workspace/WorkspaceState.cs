namespace IntoChat.OS.Modules.Flutter.Workspace;

[GenerateSerializer, Alias("ui.workspace-state")]
public sealed record WorkspaceState(
    [property: Id(0)] long Revision,
    [property: Id(1)] string[] Path,
    [property: Id(2)] string[] Open)
{
    [Id(3)] public WorkspaceViewRef[] OpenViews { get; init; } = [];
    [Id(4)] public string? FocusedViewId { get; init; }
    [Id(5)] public WorkspaceViewRef[] Pins { get; init; } = [];
    [Id(6)] public WorkspaceWindow[] Windows { get; init; } = [];
    [Id(7)] public WorkspaceViewRef[] Favourites { get; init; } = [];
}
