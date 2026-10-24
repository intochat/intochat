namespace IntoChat.OS.Flutter.Workspace;

[GenerateSerializer, Alias("ui.workspace-view-ref")]
public sealed record WorkspaceViewRef([property: Id(0)] string Id, [property: Id(1)] string App,
    [property: Id(2)] string Variant, [property: Id(3)] string Context);

[GenerateSerializer, Alias("ui.workspace-update")]
public sealed record WorkspaceUpdate([property: Id(0)] WorkspaceViewRef[] OpenViews,
    [property: Id(1)] string? FocusedViewId, [property: Id(2)] WorkspaceViewRef[] Pins)
{
    // Null keeps the stored windows, reconciled with OpenViews; Classic never sends them.
    [Id(3)] public WorkspaceWindow[]? Windows { get; init; }
    // Null keeps the stored favourites.
    [Id(4)] public WorkspaceViewRef[]? Favourites { get; init; }
}
