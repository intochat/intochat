namespace IntoChat.OS.Modules.Flutter.Workspace;

// A desktop window: its tabs are open views, and the tab id is the view id.
[GenerateSerializer, Alias("ui.workspace-window")]
public sealed record WorkspaceWindow([property: Id(0)] string Id, [property: Id(1)] WorkspaceViewRef[] Tabs,
    [property: Id(2)] string ActiveTabId);
