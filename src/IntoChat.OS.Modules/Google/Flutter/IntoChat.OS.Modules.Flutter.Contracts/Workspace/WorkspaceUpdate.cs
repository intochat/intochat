using System.Text.Json.Serialization;

namespace IntoChat.OS.Flutter.Workspace;

[GenerateSerializer, Alias("ui.workspace-view-ref")]
public sealed record WorkspaceViewRef([property: Id(0)] string Id, [property: Id(1)] string App,
    [property: Id(2)] string Variant, [property: Id(3)] string Context);

[GenerateSerializer, Alias("ui.workspace-update")]
public sealed record WorkspaceUpdate([property: Id(0)] WorkspaceViewRef[] OpenViews,
    [property: Id(1)] string? FocusedViewId, [property: Id(2)] WorkspaceViewRef[] Pins)
{
    // Null keeps the stored windows, reconciled with OpenViews; Classic never sends them. Null members are left out
    // of the JSON so a pre-window update keeps the replay digest its receipt holds.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Id(3)] public WorkspaceWindow[]? Windows { get; init; }
    // Null keeps the stored favourites.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Id(4)] public WorkspaceViewRef[]? Favourites { get; init; }
}
